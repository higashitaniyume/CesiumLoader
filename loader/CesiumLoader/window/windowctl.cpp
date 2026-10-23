// windowctl.cpp - 窗口置前控制文件通道实现(见 windowctl.h 的协议说明)
//
// 线程模型: 一个常驻线程(Sleep(100) 轮询 request.txt), 随进程退出结束 ——
// 与 speedctl 相同, 不需要清理逻辑。
// 执行环境: SetForegroundWindow / AttachThreadInput / FlashWindowEx 都在监听线程里调用,
// 不占游戏主线程, 也不改游戏自己的窗口状态(只做激活/恢复/闪烁)。
//
// 双目录: 状态文件同时写 <加载器>\window 和 %LocalAppData%\AstralParty_ModLoader\window,
// 并同时监听两处的 request.txt。mod 侧(热更程序集)定位目录只能靠环境变量和路径推导,
// 两个位置都摆一份, 只要命中其中一个通道就是通的。

#include "window/windowctl.h"

#include "config/config.h"
#include "platform/loader.h"

#include <string>
#include <cwchar>
#include <cstring>

namespace
{

const wchar_t* kRequestName = L"request.txt";
const wchar_t* kStateName = L"state.txt";

// 轮询间隔(ms): 与 speedctl 一致, 一次文件读取的代价可以忽略。
const DWORD kPollMs = 100;

// 请求文件上限: 正常内容只有十几字节, 超过这个长度的一律当垃圾。
const long long kMaxBytes = 4096;

const int kMaxDirs = 2;
std::wstring g_dirs[kMaxDirs];
int g_dir_count = 0;
volatile LONG g_started = 0;

std::wstring local_appdata_mirror()
{
    wchar_t buf[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return std::wstring();
    return std::wstring(buf) + L"\\AstralParty_ModLoader\\window";
}

std::wstring path_of(const std::wstring& dir, const wchar_t* name)
{
    return dir + L"\\" + name;
}

// 逐级创建(CreateDirectoryW 不会自动建中间层)。
bool ensure_dir(const std::wstring& dir)
{
    if (dir.empty()) return false;

    DWORD attr = GetFileAttributesW(dir.c_str());
    if (attr != INVALID_FILE_ATTRIBUTES) return (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;

    size_t slash = dir.find_last_of(L"\\/");
    if (slash != std::wstring::npos && slash > 2)   // >2 跳过 "C:\" 这种根
    {
        if (!ensure_dir(dir.substr(0, slash))) return false;
    }
    return CreateDirectoryW(dir.c_str(), nullptr) != 0;
}

bool read_text(const std::wstring& path, std::string& out)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart > kMaxBytes) { CloseHandle(h); return false; }

    std::string buf((size_t)size.QuadPart, '\0');
    DWORD read = 0;
    BOOL ok = TRUE;
    if (!buf.empty())
        ok = ReadFile(h, &buf[0], (DWORD)buf.size(), &read, nullptr);
    CloseHandle(h);
    if (!ok) return false;

    buf.resize(read);
    out.swap(buf);
    return true;
}

// 原子写: 先写 .tmp 再 MoveFileEx 覆盖, 避免 mod 侧读到"写了一半"的内容。
bool write_text(const std::wstring& path, const std::string& text)
{
    std::wstring tmp = path + L".tmp";
    HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    DWORD written = 0;
    BOOL ok = TRUE;
    if (!text.empty()) ok = WriteFile(h, text.data(), (DWORD)text.size(), &written, nullptr);
    if (ok && !text.empty()) ok = (written == text.size());
    if (ok) ok = FlushFileBuffers(h);
    CloseHandle(h);

    if (!ok) { DeleteFileW(tmp.c_str()); return false; }
    if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
    {
        DeleteFileW(tmp.c_str());
        return false;
    }
    return true;
}

void write_state()
{
    std::string s;
    s += "version="; s += cesium::config::LoaderConfig::loaderVersion; s += "\r\n";
    s += "available=1\r\n";

    for (int i = 0; i < g_dir_count; i++)
    {
        if (!write_text(path_of(g_dirs[i], kStateName), s))
            log_line(L"[windowctl] 状态文件写入失败: " + path_of(g_dirs[i], kStateName));
    }
}

bool fresh_request(const std::string& text)
{
    for (size_t i = 0; i < text.size(); i++)
    {
        char c = text[i];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
        return c != '0';
    }
    return false;
}

struct FindWindowContext
{
    HWND main;
    HWND fallback;
    LONG fallback_area;
};

// 同进程里还可能有加载器控制台、输入法/文本服务等窗口; 它们都不能当作游戏窗口。
bool excluded_class(const wchar_t* cls)
{
    if (cls == nullptr || cls[0] == L'\0') return true;
    if (_wcsicmp(cls, L"ConsoleWindowClass") == 0) return true;
    if (_wcsicmp(cls, L"PseudoConsoleWindow") == 0) return true;
    if (wcsstr(cls, L"IME") != nullptr) return true;
    if (wcsstr(cls, L"MSCTF") != nullptr) return true;
    return false;
}

BOOL CALLBACK find_window_proc(HWND hwnd, LPARAM param)
{
    FindWindowContext* ctx = reinterpret_cast<FindWindowContext*>(param);
    if (ctx == nullptr) return FALSE;

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != GetCurrentProcessId()) return TRUE;
    if (!IsWindowVisible(hwnd)) return TRUE;

    // 跳过工具窗口(Unity 的隐藏辅助窗口)
    LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    if ((ex & WS_EX_TOOLWINDOW) != 0) return TRUE;

    wchar_t cls[64] = {};
    GetClassNameW(hwnd, cls, 64);
    if (excluded_class(cls)) return TRUE;

    // Unity 播放器主窗口的类名固定; 命中就不用再比较标题/尺寸。
    if (wcscmp(cls, L"UnityWndClass") == 0)
    {
        ctx->main = hwnd;
        return FALSE;
    }

    // 兜底: 取同进程里面积最大的可见顶层窗口(排除控制台等), 避免把控制台当成游戏。
    if (GetWindowTextLengthW(hwnd) > 0)
    {
        RECT r{};
        if (GetWindowRect(hwnd, &r))
        {
            LONG w = r.right - r.left;
            LONG h = r.bottom - r.top;
            LONG area = (w > 0 && h > 0) ? w * h : 0;
            if (area > 0 && area > ctx->fallback_area)
            {
                ctx->fallback = hwnd;
                ctx->fallback_area = area;
            }
        }
    }
    return TRUE;
}

HWND find_game_window()
{
    FindWindowContext ctx{};
    EnumWindows(find_window_proc, reinterpret_cast<LPARAM>(&ctx));
    return ctx.main != nullptr ? ctx.main : ctx.fallback;
}

// 前台是否已经是游戏窗口(或游戏自己的弹窗/子窗口)。加载器控制台属于同一进程,
// 但它的前台状态不代表游戏可见, 所以控制台/输入法窗口一律不算。
bool is_foreground_game(HWND game)
{
    HWND fg = GetForegroundWindow();
    if (fg == nullptr) return false;
    if (fg == game) return true;
    if (GetAncestor(fg, GA_ROOTOWNER) == game) return true;

    DWORD pid = 0;
    GetWindowThreadProcessId(fg, &pid);
    if (pid != GetCurrentProcessId()) return false;

    wchar_t cls[64] = {};
    GetClassNameW(fg, cls, 64);
    return !excluded_class(cls);
}

bool bring_to_foreground(HWND hwnd)
{
    // 最小化的窗口先恢复, 否则 SetForegroundWindow 只会把任务栏按钮点亮。
    ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
    SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

    // Windows 的前台锁定只允许"当前前台线程"把窗口设到前台。把本线程临时附加到
    // 前台线程的输入队列, 就能在用户正在别的程序里操作时仍完成激活。
    HWND fg = GetForegroundWindow();
    DWORD fg_thread = fg != nullptr ? GetWindowThreadProcessId(fg, nullptr) : 0;
    DWORD self_thread = GetCurrentThreadId();
    bool attached = false;
    if (fg_thread != 0 && fg_thread != self_thread)
        attached = AttachThreadInput(fg_thread, self_thread, TRUE) != FALSE;

    SetForegroundWindow(hwnd);
    BringWindowToTop(hwnd);

    if (attached) AttachThreadInput(fg_thread, self_thread, FALSE);

    // 前台切换不是同步完成的, 给系统一点时间再判定, 避免误走备用路径。
    if (is_foreground_game(hwnd)) return true;
    Sleep(15);
    if (is_foreground_game(hwnd)) return true;

    // 备用路径: 未文档化但长期存在的 SwitchToThisWindow。只在 SetForegroundWindow
    // 失败时调用, 避免正常路径依赖未文档化 API。
    typedef void (WINAPI* SwitchToThisWindowFn)(HWND, BOOL);
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (user32 != nullptr)
    {
        SwitchToThisWindowFn sw = reinterpret_cast<SwitchToThisWindowFn>(
            GetProcAddress(user32, "SwitchToThisWindow"));
        if (sw != nullptr)
        {
            sw(hwnd, TRUE);
            Sleep(15);
            if (is_foreground_game(hwnd)) return true;
        }
    }

    // 系统仍拒绝(例如前台是全屏游戏/提权窗口): 闪烁任务栏提醒, 不反复抢。
    FLASHWINFO fi{};
    fi.cbSize = sizeof(fi);
    fi.hwnd = hwnd;
    fi.dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG;
    FlashWindowEx(&fi);
    return false;
}

DWORD WINAPI watch_thread(LPVOID)
{
    for (int i = 0; i < g_dir_count; i++)
        log_line(L"[windowctl] 窗口置前控制文件通道就绪 (目录: " + g_dirs[i] + L")");

    std::string last_seen[kMaxDirs];

    // 不从"启动后读到的内容"播种 last_seen: 如果 mod 恰好在监听线程启动前写下请求,
    // 本轮仍应处理它。初始 request.txt 已被写成 0, 因此第一次看到 0 只会被静默跳过。
    for (;;)
    {
        Sleep(kPollMs);

        for (int i = 0; i < g_dir_count; i++)
        {
            std::string text;
            if (!read_text(path_of(g_dirs[i], kRequestName), text)) continue;
            if (text == last_seen[i]) continue;
            last_seen[i] = text;

            if (!fresh_request(text)) continue;   // 初始的 0 或空白: 不是请求

            HWND hwnd = find_game_window();
            if (hwnd == nullptr)
            {
                log_line("[windowctl] 未找到游戏窗口, 忽略置前请求");
                continue;
            }

            if (is_foreground_game(hwnd))
            {
                log_line("[windowctl] 游戏窗口已在前台, 忽略置前请求");
                continue;
            }

            if (bring_to_foreground(hwnd))
                log_line("[windowctl] 已把游戏窗口切换到前台");
            else
                log_line("[windowctl] 置前被系统拒绝, 已闪烁任务栏");
        }
    }
    return 0;
}

} // namespace

std::wstring windowctl_dir()
{
    return loader_root() + L"\\window";
}

void windowctl_start(const std::wstring& dir)
{
    if (InterlockedExchange(&g_started, 1) != 0) return;

    g_dir_count = 0;
    if (!dir.empty()) g_dirs[g_dir_count++] = dir;

    std::wstring mirror = local_appdata_mirror();
    if (!mirror.empty())
    {
        if (mirror == dir)
            log_line("[windowctl] 镜像目录与主目录相同, 只使用一份");
        else
            g_dirs[g_dir_count++] = mirror;
    }
    else
    {
        log_line("[windowctl] 取不到 %LOCALAPPDATA%, 只使用主目录");
    }

    std::wstring kept[kMaxDirs];
    int kept_count = 0;
    for (int i = 0; i < g_dir_count; i++)
    {
        if (ensure_dir(g_dirs[i])) kept[kept_count++] = g_dirs[i];
        else log_line(L"[windowctl] 控制目录创建失败, 该目录不可用: " + g_dirs[i]);
    }
    for (int i = 0; i < kept_count; i++) g_dirs[i] = kept[i];
    g_dir_count = kept_count;

    if (g_dir_count == 0)
    {
        log_line("[windowctl] 没有可用的控制目录, 窗口置前通道不可用");
        return;
    }

    // 初始请求置 0: 上一次会话残留的令牌不会被误当成新请求。
    for (int i = 0; i < g_dir_count; i++)
    {
        if (!write_text(path_of(g_dirs[i], kRequestName), "0\r\n"))
            log_line(L"[windowctl] 初始 request.txt 写入失败: " + path_of(g_dirs[i], kRequestName));
    }
    write_state();

    HANDLE h = CreateThread(nullptr, 0, watch_thread, nullptr, 0, nullptr);
    if (h != nullptr)
    {
        CloseHandle(h);
        log_line("[windowctl] 监听目录数: " + std::to_string(g_dir_count));
        return;
    }

    // 线程起不来时删掉状态文件, SDK 的 IsAvailable 会返回 false(而不是"请求了没反应")。
    log_line("[windowctl] 监听线程创建失败, 窗口置前通道不可用");
    for (int i = 0; i < g_dir_count; i++)
        DeleteFileW(path_of(g_dirs[i], kStateName).c_str());
}
