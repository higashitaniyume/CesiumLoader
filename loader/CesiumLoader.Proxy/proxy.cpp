// proxy.cpp - Doorstop 式代理 (version.dll): 系统导出转发 + 加载器核心启动器
//
// 本 DLL 是"薄代理", 只做三件事:
//   1. 转发系统 version.dll 的 15 个导出到 C:\Windows\System32\version.dll。
//      UnityPlayer.dll 静态导入其中 3 个(GetFileVersionInfoSizeA / GetFileVersionInfoA /
//      VerQueryValueA, 经 dumpbin 确认); 其余一并转发, 因为进程里任何按名解析
//      version.dll 的模块都必须拿到真实现, 漏一个就是静默失败。
//   2. 在后台线程里加载加载器核心 CesiumLoader.dll(与本 DLL 同目录), 比对 ABI 之后
//      调用它的 CesiumLoaderStart()。
//   3. 把旧的原生附加导出(ap_speed_* / ap_console_write*)桥接到核心。
//
// 加载器主体(配置 / 变速 / fsredir / profile / steam 与 taptap 绕过 / shell / 更新检查 /
// 崩溃转储 / IL2CPP 桥 / 托管模组编排)全部在核心 DLL 里。这么拆的理由:
//   * 本 DLL 位于进程初始化关键路径上(UnityPlayer 静态导入它)。核心那一大坨
//     spdlog/fmt/json/MinHook 的静态初始化一旦出问题, 现状是"游戏起不来, 而且没有日志";
//     拆开之后关键路径上只剩这几十行, 核心晚一步由我们自己的线程加载, 崩了也只表现为
//     "游戏能起、模组不加载", 且有日志可查。
//   * 将来若要换劫持名(例如 hid.dll / winmm.dll), 只需要重写这一层与 version.def,
//     核心的 30 多个 .cpp 一行不改。
//
// 改动前务必守住的三条:
//   * **绝不把核心变成硬依赖**: 不用链接器导出转发(/export:GetFileVersionInfoA=CesiumLoader.?),
//     也不静态 import 核心。否则核心缺失 / 被杀软隔离 / 只装了代理时, UnityPlayer 解析
//     version.dll 会直接失败 —— 整个游戏无法启动。这里永远懒加载核心, 失败只记一行日志,
//     游戏照常运行。
//   * **引导线程在 DllMain 里就创建**, 不能等"首次转发调用"再触发: Astral Party 加载
//     version.dll 但从不调用其导出, 惰性触发会永远不启动。
//   * **不在 DllMain 里 LoadLibrary**: DllMain 持有 loader lock, 再进另一个 DLL 的 DllMain
//     有死锁风险。DllMain 只 CreateThread, LoadLibrary 在刚创建的那个线程里做。

#include "cesium_core_abi.h"

#include <cstdio>
#include <string>

// ============================================================================
// 一、系统 version.dll 基础设施
// ============================================================================
//
// 转发 15 个导出要用它; 诊断失败时读核心文件的版本资源也复用它(按完整路径取系统实现,
// 不链接 version.lib, 免得导入被同目录的代理接走)。

static HMODULE g_real_version = nullptr;
static HINSTANCE g_self = nullptr;            // DllMain 里记下的本模块句柄(诊断日志与 proxy_dir 共用)

static void* real_version_handle()
{
    if (g_real_version == nullptr)
    {
        g_real_version = LoadLibraryA("C:\\Windows\\System32\\version.dll");
    }
    return g_real_version;
}

static void* real_version_fn(const char* name)
{
    return reinterpret_cast<void*>(GetProcAddress(reinterpret_cast<HMODULE>(real_version_handle()), name));
}

// ============================================================================
// 二、核心没起来时的"面包屑诊断"
// ============================================================================
//
// 本 DLL 刻意不依赖核心的任何代码(日志 / 配置 / 路径都在核心里), 所以这里只用最原始的 Win32。
// 以前只写三行, "加载失败" 与 "文件不存在" 看起来一样; 现在把错误码描述、核心文件的
// 大小/属性/时间/PE 架构/版本、代理与 exe 的路径都写进去 —— 尤其是 ERROR_DLL_INIT_FAILED
// (1114): 文件明明在, 但 Windows 在初始化阶段就把它拒了, 光看 "文件存在" 没法定位。
// 位置与核心的日志同目录: <游戏 exe 目录>\AstralParty_ModLoader\logs\proxy-<会话>.log

static std::wstring error_code_text(DWORD code)
{
    if (code == 0) return L"(无错误码)";
    wchar_t sys[512] = {};
    // 固定用英文资源: 描述稳定、可 grep, 不依赖系统 UI 语言, 也不指定模块句柄(避免额外加载)。
    const DWORD n = FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                                   nullptr, code, MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US),
                                   sys, 512, nullptr);
    if (n == 0) return L"(系统无描述)";
    std::wstring out(sys, n);
    while (!out.empty() && (out.back() == L'\r' || out.back() == L'\n' || out.back() == L'.')) out.pop_back();
    return out;
}

static const wchar_t* common_error_hint(DWORD code)
{
    switch (code)
    {
    case ERROR_FILE_NOT_FOUND:     return L"找不到文件 —— 只装了代理, 或核心被移动/删除/改名。";
    case ERROR_PATH_NOT_FOUND:     return L"找不到路径 —— 加载器目录被改名或游戏 exe 目录不对。";
    case ERROR_ACCESS_DENIED:      return L"拒绝访问 —— 杀软拦截/隔离, 或文件被其它进程独占。";
    case ERROR_BAD_EXE_FORMAT:     return L"不是有效映像 —— 32/64 位不匹配, 或文件被杀软截断、下载不完整。";
    case ERROR_MOD_NOT_FOUND:      return L"缺依赖 DLL —— 常见是 VC++ 运行库(msvcp140/vcruntime140/api-ms-win-crt-*)缺失或版本不对。";
    case ERROR_DLL_INIT_FAILED:    return L"DLL 初始化失败(1114) —— 文件已找到, 但核心 DllMain / 静态初始化返回 FALSE, 或某个静态依赖初始化失败; 也可能是核心被杀软改写后结构损坏。此时游戏照常启动, 只是模组不加载。";
    case ERROR_PROC_NOT_FOUND:     return L"缺导出函数 —— 核心文件与代理不是同一版。";
    case ERROR_INVALID_IMAGE_HASH: return L"映像哈希校验失败 —— 被安全软件拦截或文件已损坏。";
    default:                       return nullptr;
    }
}

static std::wstring filetime_string(const FILETIME& ft)
{
    FILETIME local = {};
    SYSTEMTIME st = {};
    if (!FileTimeToLocalFileTime(&ft, &local) || !FileTimeToSystemTime(&local, &st)) return L"(无法解析)";
    wchar_t buf[64] = {};
    swprintf(buf, 64, L"%04u-%02u-%02u %02u:%02u:%02u",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

// 读 PE 头拿机器类型。核心被截断/替换成非 PE 文件时, 这里的失败本身就是关键证据。
static std::wstring pe_machine_text(const std::wstring& path)
{
    if (path.empty()) return L"(未提供路径)";
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return L"无法打开(读取失败)";
    IMAGE_DOS_HEADER dos = {};
    DWORD got = 0;
    BOOL ok = ReadFile(f, &dos, sizeof(dos), &got, nullptr);
    if (!ok || got != sizeof(dos) || dos.e_magic != IMAGE_DOS_SIGNATURE)
    {
        CloseHandle(f);
        return L"不是 PE(MZ 头缺失/文件可能被截断)";
    }
    // e_lfanew 可能远大于 512(链接器可用它塞 DOS stub), 按偏移 seek 过去, 只取签名 + 文件头。
    if (dos.e_lfanew < 0 || SetFilePointer(f, dos.e_lfanew, nullptr, FILE_BEGIN) == INVALID_SET_FILE_POINTER)
    {
        CloseHandle(f);
        return L"PE 头偏移无效/文件可能被截断";
    }
    struct
    {
        DWORD Signature;
        IMAGE_FILE_HEADER FileHeader;
    } nt = {};
    ok = ReadFile(f, &nt, sizeof(nt), &got, nullptr);
    CloseHandle(f);
    if (!ok || got != sizeof(nt) || nt.Signature != IMAGE_NT_SIGNATURE) return L"PE 签名无效/文件可能被截断";
    switch (nt.FileHeader.Machine)
    {
    case IMAGE_FILE_MACHINE_I386:  return L"x86 (0x014c)";
    case IMAGE_FILE_MACHINE_AMD64: return L"x64 (0x8664)";
    case IMAGE_FILE_MACHINE_ARM64: return L"ARM64 (0xaa64)";
    default:
        {
            wchar_t buf[64] = {};
            swprintf(buf, 64, L"未知机器类型 (0x%04x)", nt.FileHeader.Machine);
            return buf;
        }
    }
}

static std::wstring file_version_text(const std::wstring& path)
{
    using SizeFn = DWORD(WINAPI*)(LPCWSTR, LPDWORD);
    using InfoFn = BOOL(WINAPI*)(LPCWSTR, DWORD, DWORD, LPVOID);
    using QueryFn = BOOL(WINAPI*)(LPCVOID, LPCWSTR, LPVOID*, PUINT);
    static SizeFn size_fn = reinterpret_cast<SizeFn>(real_version_fn("GetFileVersionInfoSizeW"));
    static InfoFn info_fn = reinterpret_cast<InfoFn>(real_version_fn("GetFileVersionInfoW"));
    static QueryFn query_fn = reinterpret_cast<QueryFn>(real_version_fn("VerQueryValueW"));
    if (size_fn == nullptr || info_fn == nullptr || query_fn == nullptr) return L"(系统 version.dll 不可用)";

    DWORD handle = 0;
    const DWORD size = size_fn(path.c_str(), &handle);
    if (size == 0 || size > 1024 * 1024) return L"(无版本资源)";
    std::string data(size, '\0');
    if (!info_fn(path.c_str(), 0, size, data.data())) return L"(读版本资源失败)";

    static const wchar_t* kKeys[] = { L"ProductVersion", L"FileVersion" };
    static const wchar_t* kBlocks[] = { L"040904B0", L"000004B0" };
    for (const wchar_t* block : kBlocks)
    {
        for (const wchar_t* key : kKeys)
        {
            const std::wstring sub = std::wstring(L"\\StringFileInfo\\") + block + L"\\" + key;
            LPVOID buf = nullptr;
            UINT len = 0;
            if (!query_fn(data.data(), sub.c_str(), &buf, &len) || buf == nullptr || len == 0) continue;
            std::wstring v(static_cast<const wchar_t*>(buf), len);
            while (!v.empty() && v.back() == L'\0') v.pop_back();
            if (!v.empty()) return v;
        }
    }
    return L"(无可读版本字段)";
}

static std::wstring describe_core_file(const std::wstring& path)
{
    if (path.empty()) return L"(未提供路径)";
    WIN32_FILE_ATTRIBUTE_DATA d = {};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &d))
        return L"不存在/无法访问 (GetLastError=" + std::to_wstring(GetLastError()) + L")";

    ULARGE_INTEGER size = {};
    size.LowPart = d.nFileSizeLow;
    size.HighPart = d.nFileSizeHigh;

    std::wstring attrs;
    if (d.dwFileAttributes & FILE_ATTRIBUTE_READONLY)  attrs += L"只读 ";
    if (d.dwFileAttributes & FILE_ATTRIBUTE_HIDDEN)    attrs += L"隐藏 ";
    if (d.dwFileAttributes & FILE_ATTRIBUTE_SYSTEM)    attrs += L"系统 ";
    if (d.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) attrs += L"是目录(!) ";
    if (attrs.empty()) attrs = L"正常";

    return L"大小=" + std::to_wstring(size.QuadPart) + L" 字节; 属性=" + attrs
         + L"; 修改时间=" + filetime_string(d.ftLastWriteTime)
         + L"; PE=" + pe_machine_text(path)
         + L"; 版本=" + file_version_text(path);
}

static void log_proxy_problem(const wchar_t* what, unsigned long code,
                              const std::wstring& path, const wchar_t* hint)
{
    wchar_t exe[MAX_PATH] = {};
    if (GetModuleFileNameW(nullptr, exe, MAX_PATH) == 0) return;

    std::wstring exe_dir(exe);
    const size_t cut = exe_dir.find_last_of(L"\\/");
    if (cut == std::wstring::npos) return;
    exe_dir.resize(cut);

    const std::wstring loader_dir = exe_dir + L"\\AstralParty_ModLoader";
    const std::wstring logs_dir = loader_dir + L"\\logs";
    CreateDirectoryW(loader_dir.c_str(), nullptr);
    CreateDirectoryW(logs_dir.c_str(), nullptr);

    SYSTEMTIME st = {};
    GetLocalTime(&st);
    wchar_t name[64] = {};
    swprintf(name, 64, L"proxy-%04u%02u%02u-%02u%02u%02u.log",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);

    std::wstring self_path;
    {
        wchar_t self[MAX_PATH] = {};
        if (g_self != nullptr && GetModuleFileNameW(g_self, self, MAX_PATH) != 0) self_path = self;
    }

    wchar_t line[1024] = {};
    std::wstring text;
    text.reserve(2048);
    swprintf(line, 1024, L"\r\n[proxy] === 核心加载失败诊断 %04u-%02u-%02u %02u:%02u:%02u.%03u ===\r\n",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    text += line;
    swprintf(line, 1024, L"[proxy] 失败点: %s\r\n", what != nullptr ? what : L"(未知)");
    text += line;
    if (code != 0)
    {
        const std::wstring desc = error_code_text(code);
        swprintf(line, 1024, L"[proxy] 错误码: %lu (0x%08lX) — %s\r\n", code, code, desc.c_str());
        text += line;
        const wchar_t* auto_hint = common_error_hint(code);
        if (auto_hint != nullptr)
        {
            swprintf(line, 1024, L"[proxy] 错误码含义: %s\r\n", auto_hint);
            text += line;
        }
    }
    if (hint != nullptr && *hint != L'\0')
    {
        swprintf(line, 1024, L"[proxy] 可能原因: %s\r\n", hint);
        text += line;
    }
    swprintf(line, 1024, L"[proxy] 核心路径: %s\r\n", path.empty() ? L"(未知)" : path.c_str());
    text += line;
    text += L"[proxy] 核心文件: " + describe_core_file(path) + L"\r\n";
    const std::wstring proxy_pe = pe_machine_text(self_path);
    swprintf(line, 1024, L"[proxy] 代理: 路径=%s; ABI=%u (期望核心 ABI=%u); PE=%s\r\n",
             self_path.empty() ? L"(未知)" : self_path.c_str(),
             CESIUM_CORE_ABI_VERSION, CESIUM_CORE_ABI_VERSION, proxy_pe.c_str());
    text += line;
    swprintf(line, 1024, L"[proxy] 主程序: %s\r\n", exe);
    text += line;
    text += L"[proxy] 说明: 代理是 version.dll, 核心应与之同目录; 缺核心或版本不匹配时游戏照常运行, 只是模组不加载。\r\n";

    const std::wstring full = logs_dir + L"\\" + name;
    HANDLE file = CreateFileW(full.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;

    // 与核心日志一样按 UTF-8 落盘(便于 grep; 路径含中文时不会变成 '?')
    const int n = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                                      nullptr, 0, nullptr, nullptr);
    if (n > 0)
    {
        std::string utf8(static_cast<size_t>(n), '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                            &utf8[0], n, nullptr, nullptr);
        DWORD written = 0;
        WriteFile(file, utf8.data(), static_cast<DWORD>(n - 1), &written, nullptr);   // 不写结尾 NUL, 否则 grep 会把日志当二进制
    }
    CloseHandle(file);
}

// ============================================================================
// 三、加载器核心的加载
// ============================================================================

static HMODULE g_core = nullptr;              // 加载成功的核心模块
static volatile LONG g_core_failed = 0;       // 1 = 已失败, 不再重试(避免反复开文件 / 被 AV 反复拦)
static volatile LONG g_core_thread = 0;       // 1 = 后台加载线程已创建

// 本 DLL 所在目录 —— 核心就放在这里(游戏 exe 目录)。
// 用自身模块路径而不是主 exe 路径的原因: 冒烟测试会把两个 DLL 一起放进 bin\<配置>\ 再由测试
// 宿主 LoadLibrary, 这条路也符合同一条规则("核心与代理同目录"); 游戏里两者本来就是同一个目录。
static bool proxy_dir(std::wstring& out)
{
    wchar_t self[MAX_PATH] = {};
    if (GetModuleFileNameW(g_self, self, MAX_PATH) == 0) return false;

    std::wstring path(self);
    const size_t cut = path.find_last_of(L"\\/");
    if (cut == std::wstring::npos) return false;
    path.resize(cut);
    out.swap(path);
    return true;
}

// LoadLibrary 单独放在一个不含 C++ 对象的函数里: 本文件按 /EHsc 编译, 同一个函数里
// 既用 __try 又需要对象展开会直接编译不过(C2712)。
static HMODULE load_library_guarded(const wchar_t* path, DWORD* code, bool* threw)
{
    HMODULE mod = nullptr;
    *threw = false;
    __try
    {
        mod = LoadLibraryW(path);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *threw = true;
        *code = GetExceptionCode();
        return nullptr;
    }
    if (mod == nullptr) *code = GetLastError();
    return mod;
}

static bool load_core_once()
{
    if (g_core != nullptr) return true;
    if (InterlockedCompareExchange(&g_core_failed, 0, 0) != 0) return false;

    std::wstring dir;
    if (!proxy_dir(dir))
    {
        log_proxy_problem(L"取不到代理 DLL 所在目录", 0, L"",
                          L"GetModuleFileNameW 失败; 代理自身加载异常。");
        InterlockedExchange(&g_core_failed, 1);
        return false;
    }

    const std::wstring path = dir + L"\\" + CESIUM_CORE_DLL_NAME;
    if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        log_proxy_problem(L"加载器核心不存在(只装了代理, 没有核心 DLL)", GetLastError(), path,
                          L"代理与核心必须成对放在游戏 exe 同目录; 请整包重装加载器, 不要只复制 version.dll。");
        InterlockedExchange(&g_core_failed, 1);
        return false;
    }

    DWORD code = 0;
    bool threw = false;
    HMODULE mod = load_library_guarded(path.c_str(), &code, &threw);
    if (mod == nullptr)
    {
        if (threw)
        {
            log_proxy_problem(L"加载器核心在加载时抛异常(核心自身的 DllMain / 静态初始化出问题)",
                              code, path,
                              L"LoadLibraryW 被 SEH 异常中断; 看「核心文件」一栏的 PE 与大小是否为完整文件, 必要时整包重装。");
        }
        else
        {
            log_proxy_problem(L"加载器核心加载失败", code, path,
                              L"文件已找到但 LoadLibraryW 返回失败; 重点看「错误码含义」与「核心文件」两栏。");
        }
        InterlockedExchange(&g_core_failed, 1);
        return false;
    }

    auto abi = reinterpret_cast<CesiumCoreAbiVersionFn>(GetProcAddress(mod, "CesiumLoaderAbiVersion"));
    auto start = reinterpret_cast<CesiumCoreStartFn>(GetProcAddress(mod, "CesiumLoaderStart"));
    if (abi == nullptr || start == nullptr)
    {
        log_proxy_problem(L"加载器核心缺少契约导出(核心文件过旧或已损坏)", 0, path,
                          L"核心能加载但找不到 CesiumLoaderAbiVersion / CesiumLoaderStart; 版本不对或文件被替换, 请整包重装。");
        InterlockedExchange(&g_core_failed, 1);
        return false;
    }

    const unsigned int core_abi = abi();
    if (core_abi != CESIUM_CORE_ABI_VERSION)
    {
        wchar_t abi_hint[256] = {};
        swprintf(abi_hint, 256,
                 L"核心报告 ABI=%u, 代理期望 %u —— 不是同一次发布的产物; 请整包重装, 不要只覆盖其中一档。",
                 core_abi, CESIUM_CORE_ABI_VERSION);
        log_proxy_problem(L"加载器核心 ABI 版本不匹配(代理与核心不同步, 请整包重装)", 0, path, abi_hint);
        InterlockedExchange(&g_core_failed, 1);
        return false;
    }

    g_core = mod;
    start();   // 幂等: 重复调用只会启动一次引导线程(核心内部用 InterlockedCompareExchange 挡住)
    return true;
}

static DWORD WINAPI load_core_thread(LPVOID)
{
    // 200ms: 只是让主线程把进程初始化的静态导入阶段走完(那时 loader lock 可能还在它手里),
    // 不是等游戏。核心自己还要 Sleep(1500) 才碰任何东西, 这点延迟对引导时机没有影响。
    Sleep(200);
    load_core_once();
    return 0;
}

static void ensure_core_load_async()
{
    if (g_core != nullptr) return;
    if (InterlockedCompareExchange(&g_core_failed, 0, 0) != 0) return;
    if (InterlockedCompareExchange(&g_core_thread, 1, 0) != 0) return;

    HANDLE thread = CreateThread(nullptr, 0, load_core_thread, nullptr, 0, nullptr);
    if (thread != nullptr)
    {
        CloseHandle(thread);   // 句柄关掉, 线程继续跑
        return;
    }
    log_proxy_problem(L"创建核心加载线程失败", GetLastError(), L"",
                      L"CreateThread 失败; 进程句柄/资源耗尽或安全软件拦截。");
}

// ap_* 桥接用: 调用发生在进程跑起来之后, 这里同步确保核心已加载(而不是干等后台线程),
// 拿不到核心时返回中性值 —— 与"加载器被禁用"时的行为一致, 绝不崩溃。
static void* core_fn(const char* name)
{
    if (g_core == nullptr && !load_core_once()) return nullptr;
    return reinterpret_cast<void*>(GetProcAddress(g_core, name));
}

// ============================================================================
// 四、version.dll 15 个导出转发
// ============================================================================

extern "C" BOOL WINAPI GetFileVersionInfoA(LPCSTR lptstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData)
{
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(LPCSTR, DWORD, DWORD, LPVOID)>(real_version_fn("GetFileVersionInfoA"));
    return fn(lptstrFilename, dwHandle, dwLen, lpData);
}

extern "C" BOOL WINAPI GetFileVersionInfoByHandle(HANDLE hFile, DWORD dwLen, LPVOID lpData)
{
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(HANDLE, DWORD, LPVOID)>(real_version_fn("GetFileVersionInfoByHandle"));
    return fn(hFile, dwLen, lpData);
}

extern "C" BOOL WINAPI GetFileVersionInfoExA(DWORD dwFlags, LPCSTR lptstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData)
{
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(DWORD, LPCSTR, DWORD, DWORD, LPVOID)>(real_version_fn("GetFileVersionInfoExA"));
    return fn(dwFlags, lptstrFilename, dwHandle, dwLen, lpData);
}

extern "C" BOOL WINAPI GetFileVersionInfoExW(DWORD dwFlags, LPCWSTR lptstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData)
{
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(DWORD, LPCWSTR, DWORD, DWORD, LPVOID)>(real_version_fn("GetFileVersionInfoExW"));
    return fn(dwFlags, lptstrFilename, dwHandle, dwLen, lpData);
}

extern "C" DWORD WINAPI GetFileVersionInfoSizeA(LPCSTR lptstrFilename, LPDWORD lpdwHandle)
{
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(LPCSTR, LPDWORD)>(real_version_fn("GetFileVersionInfoSizeA"));
    return fn(lptstrFilename, lpdwHandle);
}

extern "C" DWORD WINAPI GetFileVersionInfoSizeExA(DWORD dwFlags, LPCSTR lptstrFilename, LPDWORD lpdwHandle)
{
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCSTR, LPDWORD)>(real_version_fn("GetFileVersionInfoSizeExA"));
    return fn(dwFlags, lptstrFilename, lpdwHandle);
}

extern "C" DWORD WINAPI GetFileVersionInfoSizeExW(DWORD dwFlags, LPCWSTR lptstrFilename, LPDWORD lpdwHandle)
{
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCWSTR, LPDWORD)>(real_version_fn("GetFileVersionInfoSizeExW"));
    return fn(dwFlags, lptstrFilename, lpdwHandle);
}

extern "C" DWORD WINAPI GetFileVersionInfoSizeW(LPCWSTR lptstrFilename, LPDWORD lpdwHandle)
{
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(LPCWSTR, LPDWORD)>(real_version_fn("GetFileVersionInfoSizeW"));
    return fn(lptstrFilename, lpdwHandle);
}

extern "C" BOOL WINAPI GetFileVersionInfoW(LPCWSTR lptstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData)
{
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(LPCWSTR, DWORD, DWORD, LPVOID)>(real_version_fn("GetFileVersionInfoW"));
    return fn(lptstrFilename, dwHandle, dwLen, lpData);
}

extern "C" DWORD WINAPI VerFindFileA(DWORD uFlags, LPCSTR szFileName, LPCSTR szWinDir, LPCSTR szAppDir, LPSTR szCurDir, PUINT lpuCurDirLen, LPSTR szDestDir, PUINT lpuDestDirLen)
{
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCSTR, LPCSTR, LPCSTR, LPSTR, PUINT, LPSTR, PUINT)>(real_version_fn("VerFindFileA"));
    return fn(uFlags, szFileName, szWinDir, szAppDir, szCurDir, lpuCurDirLen, szDestDir, lpuDestDirLen);
}

extern "C" DWORD WINAPI VerFindFileW(DWORD uFlags, LPCWSTR szFileName, LPCWSTR szWinDir, LPCWSTR szAppDir, LPWSTR szCurDir, PUINT lpuCurDirLen, LPWSTR szDestDir, PUINT lpuDestDirLen)
{
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCWSTR, LPCWSTR, LPCWSTR, LPWSTR, PUINT, LPWSTR, PUINT)>(real_version_fn("VerFindFileW"));
    return fn(uFlags, szFileName, szWinDir, szAppDir, szCurDir, lpuCurDirLen, szDestDir, lpuDestDirLen);
}

extern "C" DWORD WINAPI VerInstallFileA(DWORD uFlags, LPCSTR szSrcFileName, LPCSTR szDestFileName, LPCSTR szSrcDir, LPCSTR szDestDir, LPCSTR szCurDir, LPSTR szTmpFile, PUINT lpuTmpFileLen)
{
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCSTR, LPCSTR, LPCSTR, LPCSTR, LPCSTR, LPSTR, PUINT)>(real_version_fn("VerInstallFileA"));
    return fn(uFlags, szSrcFileName, szDestFileName, szSrcDir, szDestDir, szCurDir, szTmpFile, lpuTmpFileLen);
}

extern "C" DWORD WINAPI VerInstallFileW(DWORD uFlags, LPCWSTR szSrcFileName, LPCWSTR szDestFileName, LPCWSTR szSrcDir, LPCWSTR szDestDir, LPCWSTR szCurDir, LPWSTR szTmpFile, PUINT lpuTmpFileLen)
{
    static auto fn = reinterpret_cast<DWORD(WINAPI*)(DWORD, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPWSTR, PUINT)>(real_version_fn("VerInstallFileW"));
    return fn(uFlags, szSrcFileName, szDestFileName, szSrcDir, szDestDir, szCurDir, szTmpFile, lpuTmpFileLen);
}

// 注意: 系统 version.dll 不导出 VerLanguageNameA/W(Win10/11 由 kernel32 导出),
// 因此这里也不转发, 与系统导出集保持一致(共 15 个)。

extern "C" BOOL WINAPI VerQueryValueA(LPCVOID pBlock, LPCSTR lpSubBlock, LPVOID* lplpBuffer, PUINT puLen)
{
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(LPCVOID, LPCSTR, LPVOID*, PUINT)>(real_version_fn("VerQueryValueA"));
    return fn(pBlock, lpSubBlock, lplpBuffer, puLen);
}

extern "C" BOOL WINAPI VerQueryValueW(LPCVOID pBlock, LPCWSTR lpSubBlock, LPVOID* lplpBuffer, PUINT puLen)
{
    static auto fn = reinterpret_cast<BOOL(WINAPI*)(LPCVOID, LPCWSTR, LPVOID*, PUINT)>(real_version_fn("VerQueryValueW"));
    return fn(pBlock, lpSubBlock, lplpBuffer, puLen);
}

// ============================================================================
// 五、旧附加导出 → 核心桥接
// ============================================================================
//
// 这三个变速接口从 2.x 起就是原生侧调用加载器的方式(见 tools\smoke\speedtest)。
// SDK 与热更模组不走这里(热更程序集无法 P/Invoke, 用 speed\ 下的文件/UDP 通道),
// 但既然导出曾经存在过, 就继续提供, 只是实现搬到了核心里。
// 拿不到核心时返回"未启用"的中性值, 而不是让调用方崩溃或拿到假成功。

extern "C" BOOL WINAPI ap_speed_set(double speed)
{
    auto fn = reinterpret_cast<BOOL(WINAPI*)(double)>(core_fn("ap_speed_set"));
    return fn != nullptr ? fn(speed) : FALSE;
}

extern "C" double WINAPI ap_speed_get()
{
    auto fn = reinterpret_cast<double(WINAPI*)()>(core_fn("ap_speed_get"));
    return fn != nullptr ? fn() : 1.0;
}

extern "C" BOOL WINAPI ap_speed_active()
{
    auto fn = reinterpret_cast<BOOL(WINAPI*)()>(core_fn("ap_speed_active"));
    return fn != nullptr ? fn() : FALSE;
}

extern "C" void WINAPI ap_console_write(const char* msg)
{
    auto fn = reinterpret_cast<void(WINAPI*)(const char*)>(core_fn("ap_console_write"));
    if (fn != nullptr) fn(msg);
}

extern "C" void WINAPI ap_console_write_w(const wchar_t* msg, size_t len)
{
    auto fn = reinterpret_cast<void(WINAPI*)(const wchar_t*, size_t)>(core_fn("ap_console_write_w"));
    if (fn != nullptr) fn(msg, len);
}

// ============================================================================
// 六、DllMain
// ============================================================================

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_self = hinst;
        // 不在 DllMain 里 LoadLibrary(loader lock 死锁风险), 也不在这里写任何日志 ——
        // 日志设施在核心里, 而"核心加载失败"这条路径由代理自己的面包屑负责(见文件开头)。
        // 直接创建加载线程而不是等首次转发调用: 有些游戏(如 Astral Party)加载了
        // version.dll 但从不调用其导出, 惰性触发会永远不启动。
        ensure_core_load_async();
    }
    // DLL_PROCESS_DETACH 不做收尾: 摘 hook / MH_Uninitialize 由核心自己的 DllMain 负责
    // (它才是 MinHook 的持有者)。
    return TRUE;
}
