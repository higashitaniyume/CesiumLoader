// crashdump.cpp - 崩溃转储实现(见 crashdump.h)
//
// 实现要点(改之前先读完):
//   1. **在 VEH 里落盘, 不用 SetUnhandledExceptionFilter**: 后者是"最后一个机会",
//      Unity/il2cpp 自己也可能装, 谁最后装谁生效 —— 我们的过滤器很可能永远不被调用。
//      VEH 在 SEH 分发之前被调用, 无论游戏自己怎么处理后能先看到异常。
//   2. **异常处理里不分配内存、不加锁、不碰 spdlog**: 崩溃时堆/锁状态都不可信。dump 路径
//      与环境信息在 install / refresh_environment 时就格式化进静态缓冲区, 处理函数里只做
//      CreateFileW / WriteFile / MiniDumpWriteDump, 用一个 Interlocked 标志保证只落一次。
//   3. **dbghelp.dll 在 install 时加载并常驻**: 不在异常处理里 LoadLibrary(那会在
//      loader lock 里再进一次加载器, 崩溃时是死锁的经典来源)。
#include "runtime/crashdump.h"

#include "logging/logging.h"
#include "platform/build_info.h"
#include "platform/loader.h"
#include "runtime/state.h"

#include <windows.h>
#include <dbghelp.h>
#include <tlhelp32.h>
#include <winternl.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <wchar.h>

namespace
{

using MiniDumpWriteDumpFn = decltype(&MiniDumpWriteDump);

constexpr size_t kPathChars = 1024;
constexpr size_t kEnvBytes = 16384;

MiniDumpWriteDumpFn g_write_dump = nullptr;
MINIDUMP_TYPE g_dump_type = MiniDumpNormal;
wchar_t g_crash_dir[kPathChars] = {};    // <logs>\crash; 目录建不出来时回退 <logs>
wchar_t g_dump_path[kPathChars] = {};
wchar_t g_note_path[kPathChars] = {};
char g_env[kEnvBytes] = {};
volatile LONG g_env_len = 0;             // 只有 0 或完整长度, 见 publish_environment
volatile LONG g_dumping = 0;
bool g_veh = false;                      // 致命异常处理器是否已装(按需转储不需要它)

// ---------- 文本小工具(只在正常运行时调用, 可以用堆/标准库) ----------

std::string narrow(const std::wstring& w)
{
    if (w.empty()) return std::string();
    const int len = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    if (len <= 0) return std::string();
    std::string s((size_t)len, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), s.data(), len, nullptr, nullptr);
    return s;
}

std::string narrow(const wchar_t* w)
{
    return w == nullptr ? std::string() : narrow(std::wstring(w));
}

const char* yesno(bool v)
{
    return v ? "true" : "false";
}

std::string now_text()
{
    SYSTEMTIME st{};
    GetLocalTime(&st);
    char buf[64] = {};
    sprintf_s(buf, "%04d-%02d-%02d %02d:%02d:%02d.%03d",
              st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    return buf;
}

std::string uptime_text()
{
    FILETIME create{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &create, &exit, &kernel, &user)) return "-";

    FILETIME now{};
    GetSystemTimeAsFileTime(&now);
    ULARGE_INTEGER a{}, b{};
    a.LowPart = create.dwLowDateTime;  a.HighPart = create.dwHighDateTime;
    b.LowPart = now.dwLowDateTime;     b.HighPart = now.dwHighDateTime;
    const ULONGLONG ms = (b.QuadPart - a.QuadPart) / 10000ULL;

    char buf[64] = {};
    if (ms < 60000ULL) sprintf_s(buf, "%.1fs", (double)ms / 1000.0);
    else sprintf_s(buf, "%llum %02llus", ms / 60000ULL, (ms % 60000ULL) / 1000ULL);
    return buf;
}

std::string os_text()
{
    using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
    RTL_OSVERSIONINFOW vi{};
    vi.dwOSVersionInfoSize = sizeof(vi);

    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    auto fn = ntdll == nullptr ? nullptr
                               : reinterpret_cast<RtlGetVersionFn>(
                                     reinterpret_cast<void*>(GetProcAddress(ntdll, "RtlGetVersion")));
    if (fn != nullptr) fn(&vi);

    const char* name = "Windows";
    if (vi.dwMajorVersion == 10) name = vi.dwBuildNumber >= 22000 ? "Windows 11" : "Windows 10";
    else if (vi.dwMajorVersion == 6) name = vi.dwMinorVersion >= 3 ? "Windows 8.1" : "Windows 8";

    SYSTEM_INFO si{};
    GetNativeSystemInfo(&si);
    const char* arch = "x64";
    if (si.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_INTEL) arch = "x86";
    else if (si.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_ARM64) arch = "arm64";

    MEMORYSTATUSEX mem{};
    mem.dwLength = sizeof(mem);

    // Win10/11 的主版本号都是 10.0, 只有 build 能区分; 老系统才带主次版本号。
    char version[64] = {};
    if (vi.dwMajorVersion >= 10)
        sprintf_s(version, "%s (build %lu)", name, vi.dwBuildNumber);
    else
        sprintf_s(version, "%s %lu.%lu (build %lu)", name, vi.dwMajorVersion, vi.dwMinorVersion,
                  vi.dwBuildNumber);

    char buf[192] = {};
    if (GlobalMemoryStatusEx(&mem))
        sprintf_s(buf, "%s · %s · %lu cores · %.1f GB RAM", version, arch,
                  si.dwNumberOfProcessors, (double)mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0));
    else
        sprintf_s(buf, "%s · %s · %lu cores", version, arch, si.dwNumberOfProcessors);
    return buf;
}

std::string file_stamp(const std::wstring& path)
{
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad)) return "(not found)";

    const ULONGLONG size = ((ULONGLONG)fad.nFileSizeHigh << 32) | fad.nFileSizeLow;

    FILETIME local{};
    SYSTEMTIME st{};
    FileTimeToLocalFileTime(&fad.ftLastWriteTime, &local);
    FileTimeToSystemTime(&local, &st);

    char buf[128] = {};
    sprintf_s(buf, "%llu B · %04d-%02d-%02d %02d:%02d:%02d",
              size, st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

std::string cmdline_text()
{
    const wchar_t* cmd = GetCommandLineW();
    std::string text = narrow(cmd);
    if (text.size() > 400) text = text.substr(0, 400) + "...";
    return text;
}

std::string main_module_path()
{
    wchar_t buf[kPathChars] = {};
    const DWORD n = GetModuleFileNameW(nullptr, buf, (DWORD)(sizeof(buf) / sizeof(buf[0])));
    return narrow(std::wstring(buf, n));
}

std::wstring parent_dir(const std::wstring& path)
{
    const size_t pos = path.find_last_of(L"\\/");
    return pos == std::wstring::npos ? std::wstring() : path.substr(0, pos);
}

std::wstring file_name_of(const std::wstring& path)
{
    const size_t pos = path.find_last_of(L"\\/");
    return pos == std::wstring::npos ? path : path.substr(pos + 1);
}

// 一行 "标签   : 值"(标签补到 12 列, 和加载器日志的列宽一致)
void add_line(std::string& out, const char* label, const std::string& value)
{
    out += label;
    for (size_t i = strlen(label); i < 12; ++i) out += ' ';
    out += ": ";
    out += value;
    out += "\r\n";
}

std::string speed_text(double v)
{
    char buf[32] = {};
    sprintf_s(buf, "%.10g", v);
    return buf;
}

std::string config_text(const LoaderConfig& cfg)
{
    std::string out;
    out += "  enabled=" + std::string(yesno(cfg.enabled)) +
           " consoleEnabled=" + yesno(cfg.consoleEnabled) +
           " consoleTopmost=" + yesno(cfg.consoleTopmost) +
           " debugShell=" + yesno(cfg.debugShell) +
           " forwardActivityLog=" + yesno(cfg.forwardActivityLog) + "\r\n";
    out += "  debugWaitForDebugger=" + std::string(yesno(cfg.debugWaitForDebugger)) +
           " debugWaitTimeoutSec=" + std::to_string(cfg.debugWaitTimeoutSec) +
           " logLevel=" + cfg.logLevel + "\r\n";
    out += "  crashDumpEnabled=" + std::string(yesno(cfg.crashDumpEnabled)) +
           " crashDumpFullMemory=" + yesno(cfg.crashDumpFullMemory) + "\r\n";
    out += "  speedhackBaseSpeed=" + speed_text(cfg.speedhackBaseSpeed) +
           " speedControlEnabled=" + yesno(cfg.speedControlEnabled) +
           " windowControlEnabled=" + yesno(cfg.windowControlEnabled) +
           " audioControlEnabled=" + yesno(cfg.audioControlEnabled) + "\r\n";
    out += "  profile=\"" + cfg.profile + "\" profileWindowTitle=" + yesno(cfg.profileWindowTitle) +
           " useManagedBootstrap=" + yesno(cfg.useManagedBootstrap) + "\r\n";
    out += "  gameAssemblyTimeoutSec=" + std::to_string(cfg.gameAssemblyTimeoutSec) +
           " domainTimeoutSec=" + std::to_string(cfg.domainTimeoutSec) +
           " hybridclrTimeoutSec=" + std::to_string(cfg.hybridclrTimeoutSec) + "\r\n";
    out += "  steamBypassEnabled=" + std::string(yesno(cfg.steamBypassEnabled)) +
           " restartCheck=" + yesno(cfg.steamBypassRestartCheck) +
           " matchmaking=" + yesno(cfg.steamBypassMatchmaking) +
           " lobbyHasValue=" + yesno(cfg.steamBypassLobbyHasValue) +
           " lobbyMethods=" + yesno(cfg.steamBypassLobbyMethods) +
           " lobbyQuery=" + yesno(cfg.steamBypassLobbyQuery) +
           " taskCtorMode=" + cfg.steamBypassTaskCtorMode + "\r\n";
    out += "  tapTapBypassEnabled=" + std::string(yesno(cfg.tapTapBypassEnabled)) +
           " restartCheck=" + yesno(cfg.tapTapBypassRestartCheck) +
           " login=" + yesno(cfg.tapTapBypassLogin) + "\r\n";
    return out;
}

// 环境信息整体: 版本 / 路径 / 命令行 / 关键二进制指纹 / 配置 / mod 加载结果。
// 故意做成"纯文本、一行一个事实", 用户直接贴出来就能定位版本与环境问题。
std::string build_environment(const LoaderConfig* override_cfg)
{
    const cesium::runtime::Snapshot snap = cesium::runtime::snapshot();
    const LoaderConfig& cfg = override_cfg != nullptr ? *override_cfg : snap.cfg;

    const std::wstring root = loader_root();
    const std::wstring game_dir = parent_dir(root);
    const std::wstring exe = snap.exe_path.empty() ? std::wstring() : snap.exe_path;
    const std::string exe_name = exe.empty() ? std::string("(unknown)") : narrow(file_name_of(exe));
    const std::string game_ver = snap.game_version.empty() ? std::string("-") : snap.game_version;
    const std::string unity_ver_text = snap.unity_version.empty() ? std::string("-") : snap.unity_version;

    std::string out;
    out += "===== CesiumLoader environment =====\r\n";
    add_line(out, "time", now_text());
    add_line(out, "session", narrow(cesium::log::session_id()));
    add_line(out, "process", "pid " + std::to_string(GetCurrentProcessId()) + " · uptime " + uptime_text());
    add_line(out, "os", os_text());
    add_line(out, "loader", "CesiumLoader " + std::string(LoaderConfig::loaderVersion) +
                             " (commit " + CESIUM_BUILD_COMMIT + ") · SDK " + cfg.sdkVersion);
    add_line(out, "game", exe_name + " " + game_ver + " · Unity " + unity_ver_text);
    add_line(out, "exe", exe.empty() ? main_module_path() : narrow(exe));
    add_line(out, "root", narrow(root));
    add_line(out, "game dir", narrow(game_dir));
    add_line(out, "crash dir", narrow(g_crash_dir));
    add_line(out, "config", narrow(config_path()));
    add_line(out, "logs", narrow(logs_dir()));
    add_line(out, "sdk", narrow(sdk_dir()));
    add_line(out, "bootstrap", narrow(bootstrap_dir()));
    add_line(out, "mods dir", narrow(mods_dir()) + " (" + std::to_string(snap.mods.size()) + " 个已扫描)");
    add_line(out, "cmdline", cmdline_text());

    // 关键二进制指纹: GameAssembly.dll 的大小+修改时间能唯一对上一次次热更, UnityPlayer 带版本资源。
    out += "\r\n[binaries]\r\n";
    const std::wstring game_assembly = game_dir + L"\\GameAssembly.dll";
    const std::wstring unity_player = game_dir + L"\\UnityPlayer.dll";
    add_line(out, "GameAssembly", file_stamp(game_assembly));
    const std::wstring unity_ver = file_version(unity_player);
    add_line(out, "UnityPlayer", (unity_ver.empty() ? std::string() : narrow(unity_ver) + " · ") +
                                   file_stamp(unity_player));

    out += "\r\n[config]\r\n";
    out += config_text(cfg);

    out += "\r\n[mods]\r\n";
    if (snap.mods.empty())
    {
        out += "  (崩溃时还没有 mod 加载结果)\r\n";
    }
    else
    {
        for (const auto& m : snap.mods)
        {
            // 留出余量给末尾的 nullptr/换行, 超出就明确截断(而不是写出半行)。
            if (out.size() + m.id.size() + m.detail.size() + 64 > kEnvBytes)
            {
                out += "  ... (truncated)\r\n";
                break;
            }
            out += "  " + m.id + "  " + (m.version.empty() ? "-" : m.version) + "  " + m.detail + "\r\n";
        }
    }
    return out;
}

// 把环境信息发布到静态缓冲区: **先清零长度再拷贝**, 异常处理里只读完整的快照。
void publish_environment(const LoaderConfig* override_cfg)
{
    std::string text = build_environment(override_cfg);
    if (text.size() >= kEnvBytes) text.resize(kEnvBytes - 1);

    InterlockedExchange(&g_env_len, 0);
    memcpy(g_env, text.data(), text.size());
    g_env[text.size()] = '\0';
    InterlockedExchange(&g_env_len, (LONG)text.size());
}

// ---------- 异常处理(这里只能用栈 + CreateFile/WriteFile) ----------

const char* exception_name(DWORD code)
{
    switch (code)
    {
    case 0xC0000005: return "EXCEPTION_ACCESS_VIOLATION";
    case 0xC000001D: return "EXCEPTION_ILLEGAL_INSTRUCTION";
    case 0xC0000094: return "EXCEPTION_INT_DIVIDE_BY_ZERO";
    case 0xC00000FD: return "EXCEPTION_STACK_OVERFLOW";
    case 0xC0000374: return "STATUS_HEAP_CORRUPTION";
    case 0xC0000409: return "STATUS_STACK_BUFFER_OVERRUN";
    default:         return "unknown";
    }
}

bool is_fatal(DWORD code)
{
    switch (code)
    {
    case 0xC0000005:
    case 0xC000001D:
    case 0xC0000094:
    case 0xC00000FD:
    case 0xC0000374:
    case 0xC0000409:
        return true;
    default:
        return false;
    }
}

// 上次崩溃留下的转储: 崩溃时进程直接退出, 控制台来不及提示"文件在哪"; 下次启动时在这里
// 点一次名, 用户不用自己翻目录(这就是"我咋没看到 dmp"最常见的场景)。
// 只在正常运行时调用(FindFirstFileW), 异常处理里不会走到这里。
void log_previous_dumps(const std::wstring& crash_dir)
{
    WIN32_FIND_DATAW fd{};
    HANDLE find = FindFirstFileW((crash_dir + L"\\*.dmp").c_str(), &fd);
    if (find == INVALID_HANDLE_VALUE) return;

    int count = 0;
    std::wstring newest;
    ULARGE_INTEGER newest_time{};
    do
    {
        ++count;
        ULARGE_INTEGER t{};
        t.LowPart = fd.ftLastWriteTime.dwLowDateTime;
        t.HighPart = fd.ftLastWriteTime.dwHighDateTime;
        if (t.QuadPart > newest_time.QuadPart)
        {
            newest_time = t;
            newest = fd.cFileName;
        }
    } while (FindNextFileW(find, &fd));
    FindClose(find);

    if (count <= 0) return;

    // 转储文件名就是日志会话标识 <yyyyMMdd>-<HHmmss>-<pid>, 和同目录的 .txt 一一对应。
    console_set_color(LOG_YELLOW);
    log_line(L"[crash] " + std::to_wstring(count) + L" 个历史转储在 " + short_path(crash_dir) +
             L"\\ (最新: " + newest + L")");
    console_set_color(LOG_DEFAULT);
}

// 崩溃说明(.txt): 异常摘要 + 之前准备好的环境信息。
void write_crash_note(const EXCEPTION_RECORD* rec)
{
    if (g_note_path[0] == L'\0' || rec == nullptr) return;

    HANDLE h = CreateFileW(g_note_path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME st{};
    GetLocalTime(&st);

    char head[1024] = {};
    int n = sprintf_s(head,
                      "===== CesiumLoader crash report =====\r\n"
                      "time      : %04d-%02d-%02d %02d:%02d:%02d.%03d\r\n"
                      "exception : 0x%08lX (%s)\r\n"
                      "address   : %p\r\n"
                      "thread    : %lu\r\n",
                      st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                      (unsigned long)rec->ExceptionCode, exception_name(rec->ExceptionCode),
                      rec->ExceptionAddress, (unsigned long)GetCurrentThreadId());
    if (n < 0) n = 0;

    // 访问冲突的两个参数: [0] = 0 读 / 1 写 / 8 执行, [1] = 出错地址。
    if (rec->ExceptionCode == 0xC0000005 && rec->NumberParameters >= 2 &&
        n > 0 && n < (int)sizeof(head) - 96)
    {
        char detail[96] = {};
        sprintf_s(detail, "detail    : %s to 0x%p\r\n",
                  rec->ExceptionInformation[0] == 1 ? "write" :
                  (rec->ExceptionInformation[0] == 8 ? "execute" : "read"),
                  (void*)rec->ExceptionInformation[1]);
        strcat_s(head, detail);
    }

    if (n > 0 && n < (int)sizeof(head) - 512)
    {
        char path[512] = {};
        const int used = (int)strlen(head);
        const int room = (int)sizeof(head) - used - 1;
        const int written = WideCharToMultiByte(CP_UTF8, 0, g_dump_path, -1, path, (int)sizeof(path) - 1,
                                                nullptr, nullptr);
        if (written > 0 && room > 32)
            sprintf_s(head + used, (size_t)room, "dump      : %s\r\n\r\n", path);
        else
            strcat_s(head, "\r\n");
    }

    DWORD written = 0;
    WriteFile(h, head, (DWORD)strlen(head), &written, nullptr);

    const LONG env_len = g_env_len;
    if (env_len > 0) WriteFile(h, g_env, (DWORD)env_len, &written, nullptr);

    CloseHandle(h);
}

LONG WINAPI on_exception(EXCEPTION_POINTERS* info)
{
    if (info == nullptr || info->ExceptionRecord == nullptr) return EXCEPTION_CONTINUE_SEARCH;

    // 有调试器附加时一律不接管: 断点/第一机会异常都该由调试器停下, 我们不抢现场。
    if (IsDebuggerPresent()) return EXCEPTION_CONTINUE_SEARCH;
    if (!is_fatal(info->ExceptionRecord->ExceptionCode)) return EXCEPTION_CONTINUE_SEARCH;
    if (g_write_dump == nullptr || g_dump_path[0] == L'\0') return EXCEPTION_CONTINUE_SEARCH;
    if (InterlockedCompareExchange(&g_dumping, 1, 0) != 0) return EXCEPTION_CONTINUE_SEARCH;

    HANDLE file = CreateFileW(g_dump_path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        MINIDUMP_EXCEPTION_INFORMATION mei{};
        mei.ThreadId = GetCurrentThreadId();
        mei.ExceptionPointers = info;
        mei.ClientPointers = FALSE;
        g_write_dump(GetCurrentProcess(), GetCurrentProcessId(), file, g_dump_type,
                     &mei, nullptr, nullptr);
        CloseHandle(file);
    }

    write_crash_note(info->ExceptionRecord);

    // 现场已经留下, 后续照常交给游戏自己的处理器与 WER —— 不改变崩溃语义。
    return EXCEPTION_CONTINUE_SEARCH;
}

// ---------- 按需转储的辅助(正常运行时, 可以分配内存/用标准库) ----------

std::string hex_text(unsigned long long value)
{
    char buf[32] = {};
    sprintf_s(buf, "0x%016llX", value);
    return buf;
}

// 线程列表: tid + 是否主线程。看门狗命中时主线程栈是重点, 这里先给出线程清单,
// 分析时用 cdb 的 ~*k 逐个看(主线程那条会标 (main))。
std::string threads_text(unsigned long main_tid)
{
    std::string out;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return "  (thread snapshot failed)\r\n";

    THREADENTRY32 te{};
    te.dwSize = sizeof(te);
    int count = 0;
    if (Thread32First(snap, &te))
    {
        do
        {
            if (te.th32OwnerProcessID != GetCurrentProcessId()) continue;
            ++count;
            out += "  tid " + std::to_string(te.th32ThreadID);
            if (te.th32ThreadID == main_tid) out += "  (main)";
            out += "\r\n";
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);

    if (count == 0) out = "  (none)\r\n";
    return out;
}

std::string sanitize_kind(const char* kind)
{
    std::string raw = (kind == nullptr || *kind == '\0') ? std::string("manual") : std::string(kind);
    std::string out;
    for (char c : raw)
    {
        const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                        c == '_' || c == '-';
        if (ok) out.push_back(c);
        if (out.size() >= 16) break;
    }
    return out.empty() ? std::string("manual") : out;
}

bool write_text_file(const std::wstring& path, const std::string& text)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    const BOOL ok = WriteFile(h, text.data(), (DWORD)text.size(), &written, nullptr);
    CloseHandle(h);
    return ok != FALSE && written == text.size();
}

// CreateDirectoryW 不会建中间层: 逐级建(已存在视为成功)。转储目录可能在首次运行/
// 探针里连父目录都不存在, 不能因此退回 logs 根目录。
void create_dir_tree(const std::wstring& path)
{
    std::wstring partial;
    for (size_t i = 0; i < path.size(); ++i)
    {
        partial.push_back(path[i]);
        const bool separator = (path[i] == L'\\' || path[i] == L'/');
        if (separator && partial.size() > 3) CreateDirectoryW(partial.c_str(), nullptr);
    }
    CreateDirectoryW(path.c_str(), nullptr);
}

// 转储目录准备好(建目录 / 加载 dbghelp / 记环境信息)。致命异常与按需转储共用。
// 位置: <loader>\crash —— 转储是大文件, 和 logs\ 里的日志分开, 用户也更好找。
bool ensure_ready(const LoaderConfig& cfg, bool full_memory)
{
    if (g_write_dump != nullptr && g_crash_dir[0] != L'\0') return true;

    const std::wstring root = loader_root();
    if (root.empty()) return false;

    if (g_write_dump == nullptr)
    {
        HMODULE dbghelp = LoadLibraryW(L"dbghelp.dll");
        if (dbghelp == nullptr) return false;
        g_write_dump = reinterpret_cast<MiniDumpWriteDumpFn>(
            reinterpret_cast<void*>(GetProcAddress(dbghelp, "MiniDumpWriteDump")));
        if (g_write_dump == nullptr) return false;
    }

    if (g_crash_dir[0] == L'\0')
    {
        // 转储目录: <loader>\crash。加载器目录可能还不存在(探针/首次运行), 先逐级建出来;
        // 仍建不出来(权限/只读)才退回加载器根目录, 不因为目录问题放弃转储。
        create_dir_tree(root);
        const std::wstring crash_dir = root + L"\\crash";
        if (crash_dir.size() < kPathChars &&
            (CreateDirectoryW(crash_dir.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS))
            wcscpy_s(g_crash_dir, crash_dir.c_str());
        else
            wcscpy_s(g_crash_dir, root.c_str());
    }

    g_dump_type = full_memory
        ? static_cast<MINIDUMP_TYPE>(MiniDumpWithFullMemory | MiniDumpWithFullMemoryInfo |
                                     MiniDumpWithHandleData | MiniDumpWithThreadInfo |
                                     MiniDumpWithUnloadedModules)
        : static_cast<MINIDUMP_TYPE>(MiniDumpNormal | MiniDumpWithThreadInfo);

    // 环境信息先写进静态缓冲区(此时版本号还没拿到, refresh_environment 会补)。
    publish_environment(&cfg);
    return true;
}

} // namespace

namespace cesium::crashdump
{

void install(const LoaderConfig& cfg, bool enabled, bool full_memory)
{
    // 即使 fatal 转储被关掉也要把目录/dbghelp 准备好 —— 看门狗的按需转储共用这套设施。
    if (!ensure_ready(cfg, full_memory))
    {
        log_line("[crash] dumps unavailable (dbghelp.dll or loader directory); fatal handler not installed");
        return;
    }

    if (!enabled)
    {
        log_line("[crash] fatal-exception handler disabled (crashDumpEnabled=false);"
                 " on-demand dumps (shell 'dump', watchdog) still available");
        return;
    }
    if (g_veh) return;                            // 幂等: 只装一次

    // 路径必须在 install 时算好: 异常处理里不能拼 std::wstring(要分配内存)。
    // 会话标识在 DllMain 打版本横幅时就已经定下, 这里拿到的与日志文件是同一个会话。
    const std::wstring base = std::wstring(g_crash_dir) + L"\\" + cesium::log::session_id();
    if (base.size() + 8 >= kPathChars)
    {
        log_line("[crash] crash path too long; fatal handler not installed");
        return;
    }
    wcscpy_s(g_dump_path, (base + L".dmp").c_str());
    wcscpy_s(g_note_path, (base + L".txt").c_str());

    if (AddVectoredExceptionHandler(1, on_exception) == nullptr)
    {
        log_line("[crash] AddVectoredExceptionHandler failed; fatal handler not installed");
        return;
    }
    g_veh = true;

    console_set_color(LOG_DIM);
    // short_path() 返回宽字符, 与窄字面量不能直接相加 —— 整行按宽字符拼再交给宽重载。
    log_line(std::wstring(L"[crash] minidump handler installed: ") + short_path(g_crash_dir) +
             (full_memory ? L" (full memory)" : L" (thread stacks only)"));
    console_set_color(LOG_DEFAULT);

    // 上一次崩溃留下的转储在这里点名, 免得用户找不到文件。
    log_previous_dumps(g_crash_dir);
}

void refresh_environment()
{
    if (g_write_dump == nullptr) return;
    publish_environment(nullptr);
}

std::wstring crash_dir()
{
    return std::wstring(g_crash_dir);
}

std::wstring write_dump_now(const char* kind, const char* source, const char* note,
                            long long stall_ms, unsigned long long rip, unsigned long long rsp,
                            unsigned long main_tid)
{
    if (g_write_dump == nullptr || g_crash_dir[0] == L'\0') return std::wstring();

    // 与异常路径互斥: 同一时刻只写一份(memory dump 不是一个能并行的操作)。
    if (InterlockedCompareExchange(&g_dumping, 1, 0) != 0) return std::wstring();

    const std::string tag = sanitize_kind(kind);
    const std::wstring session = cesium::log::session_id();
    std::wstring base = std::wstring(g_crash_dir) + L"\\" + std::wstring(tag.begin(), tag.end()) + L"-" + session;

    // 同一次启动里可能被请求多次(反复卡/手动取证): 找第一个没被占用的名字。
    std::wstring candidate = base;
    for (int i = 2; i <= 99; ++i)
    {
        if (GetFileAttributesW((candidate + L".dmp").c_str()) == INVALID_FILE_ATTRIBUTES) break;
        candidate = base + L"-" + std::to_wstring(i);
    }
    if (candidate.size() + 8 >= kPathChars)
    {
        InterlockedExchange(&g_dumping, 0);
        return std::wstring();
    }

    const std::wstring dump_path = candidate + L".dmp";
    const std::wstring note_path = candidate + L".txt";

    bool dumped = false;
    HANDLE file = CreateFileW(dump_path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        dumped = g_write_dump(GetCurrentProcess(), GetCurrentProcessId(), file, g_dump_type,
                              nullptr, nullptr, nullptr) != FALSE;
        CloseHandle(file);
    }
    if (!dumped)
    {
        InterlockedExchange(&g_dumping, 0);
        return std::wstring();
    }

    // 说明文件: 事件摘要 + 请求信息 + 线程列表 + 同一份环境信息(与崩溃转储一致)。
    std::string text;
    text += "===== CesiumLoader " + tag + " dump =====\r\n";
    text += "time      : " + now_text() + "\r\n";
    text += "session   : " + narrow(session) + "\r\n";
    text += "dump      : " + narrow(dump_path) + "\r\n";
    text += "\r\n[request]\r\n";
    text += "reason    : " + tag + "\r\n";
    if (source != nullptr && *source != '\0') text += "source    : " + std::string(source) + "\r\n";
    if (stall_ms > 0)
    {
        char sec[32] = {};
        sprintf_s(sec, "%.1fs", (double)stall_ms / 1000.0);
        text += std::string("stall     : ") + sec + "\r\n";
    }
    if (rip != 0) text += "rip       : " + hex_text(rip) + "\r\n";
    if (rsp != 0) text += "rsp       : " + hex_text(rsp) + "\r\n";
    if (main_tid != 0) text += "main tid  : " + std::to_string(main_tid) + "\r\n";
    if (note != nullptr && *note != '\0') text += "note      : " + std::string(note) + "\r\n";
    text += "\r\n";

    text += build_environment(nullptr);
    text += "\r\n[threads]\r\n";
    text += threads_text(main_tid);

    write_text_file(note_path, text);

    InterlockedExchange(&g_dumping, 0);
    return dump_path;
}

} // namespace cesium::crashdump
