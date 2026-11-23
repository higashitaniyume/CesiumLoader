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
// 一、核心没起来时的"面包屑"
// ============================================================================
//
// 本 DLL 刻意不依赖核心的任何代码(日志 / 配置 / 路径都在核心里), 所以这里用最原始的 Win32
// 写一行。没有它的话, "核心加载失败" 的表现就是"模组莫名其妙不加载", 用户侧没有任何证据。
// 位置与核心的日志同目录: <游戏 exe 目录>\AstralParty_ModLoader\logs\proxy-<会话>.log
static void log_proxy_problem(const wchar_t* what, unsigned long code, const std::wstring& path)
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

    const std::wstring full = logs_dir + L"\\" + name;
    HANDLE file = CreateFileW(full.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;

    wchar_t line[2048] = {};
    swprintf(line, 2048,
             L"[proxy] %s (code=%lu, 0x%08lX)\r\n"
             L"[proxy] 核心路径: %s\r\n"
             L"[proxy] 代理是 version.dll, 核心应与之同目录; 缺核心或版本不匹配时游戏照常运行, 只是模组不加载。\r\n",
             what, code, code, path.empty() ? L"(未知)" : path.c_str());

    // 与核心日志一样按 UTF-8 落盘(便于 grep; 路径含中文时不会变成 '?')
    char utf8[4096] = {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8), nullptr, nullptr);
    DWORD written = 0;
    if (n > 1) WriteFile(file, utf8, (DWORD)(n - 1), &written, nullptr);
    CloseHandle(file);
}

// ============================================================================
// 二、系统 version.dll 转发基础设施
// ============================================================================

static HMODULE g_real_version = nullptr;

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
// 三、加载器核心的加载
// ============================================================================

static HINSTANCE g_self = nullptr;            // DllMain 里记下的本模块句柄
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
        log_proxy_problem(L"取不到代理 DLL 所在目录", 0, L"");
        InterlockedExchange(&g_core_failed, 1);
        return false;
    }

    const std::wstring path = dir + L"\\" + CESIUM_CORE_DLL_NAME;
    if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        log_proxy_problem(L"加载器核心不存在(只装了代理, 没有核心 DLL)", GetLastError(), path);
        InterlockedExchange(&g_core_failed, 1);
        return false;
    }

    DWORD code = 0;
    bool threw = false;
    HMODULE mod = load_library_guarded(path.c_str(), &code, &threw);
    if (mod == nullptr)
    {
        log_proxy_problem(threw ? L"加载器核心在加载时抛异常(核心自身的 DllMain / 静态初始化出问题)"
                                : L"加载器核心加载失败",
                          code, path);
        InterlockedExchange(&g_core_failed, 1);
        return false;
    }

    auto abi = reinterpret_cast<CesiumCoreAbiVersionFn>(GetProcAddress(mod, "CesiumLoaderAbiVersion"));
    auto start = reinterpret_cast<CesiumCoreStartFn>(GetProcAddress(mod, "CesiumLoaderStart"));
    if (abi == nullptr || start == nullptr)
    {
        log_proxy_problem(L"加载器核心缺少契约导出(核心文件过旧或已损坏)", 0, path);
        InterlockedExchange(&g_core_failed, 1);
        return false;
    }

    const unsigned int core_abi = abi();
    if (core_abi != CESIUM_CORE_ABI_VERSION)
    {
        log_proxy_problem(L"加载器核心 ABI 版本不匹配(代理与核心不同步, 请整包重装)", core_abi, path);
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
    log_proxy_problem(L"创建核心加载线程失败", GetLastError(), L"");
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
