// windowctl.cpp - 窗口置前控制通道实现(见 windowctl.h 的协议说明)
//
// 线程模型: 一个常驻线程(blocking recvfrom + 超时轮询), 随进程退出结束 ——
// 与 speedctl 相同, 不需要清理逻辑。
// 执行环境: SetForegroundWindow / AttachThreadInput / FlashWindowEx 都在监听线程里调用,
// 不占游戏主线程, 也不改游戏自己的窗口状态(只做激活/恢复/闪烁)。
//
// 为什么是每进程一个本地 UDP 端口:
//   文件通道的问题在多开时暴露: 所有 shell 外壳 junction 到同一份
//   <游戏目录>\AstralParty_ModLoader, request.txt 天然全局共享, 一个进程
//   的"轮到我"会被另一个进程的加载器取走, 启动时的重置还会互相清请求。
//   UDP 改为每进程 bind 127.0.0.1:0(临时端口), 端口通过 CESIUM_FOCUS_PORT
//   告诉同进程的托管代码——每个进程只收自己的 datagram, 天然隔离。
//   (与 audioctl 同款模式: bind 0 + getsockname + 环境变量发现。)
//
// 提示音: 报文第 2 行可选一个绝对路径(UTF-8), 加载器在置前的同时用
// PlaySound 异步播放这个 WAV。放在同一个请求里, 是为了让"窗口已在前台"这条
// 静默规则同时作用于窗口和声音。

#include "window/windowctl.h"
#include "window/request.h"

#include "config/config.h"
#include "platform/loader.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <mmsystem.h>   // PlaySoundW (WIN32_LEAN_AND_MEAN 不会带进来)

#include <string>
#include <cwchar>
#include <cstring>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "winmm.lib")

namespace
{

// 提示音上限: 一段 1-2 秒的 WAV 通常 < 1MB, 超过 16MB 的多半是放错文件了。
const long long kMaxSoundBytes = 16ll * 1024 * 1024;

// UDP datagram 上限: 令牌 + 绝对路径(4096) + 开关, 超过这个长度的一律当垃圾。
const size_t kMaxDatagramBytes = 8192;

volatile LONG g_started = 0;
SOCKET g_socket = INVALID_SOCKET;
int g_bound_port = 0;

std::wstring utf8_to_wide(const std::string& text)
{
    if (text.empty()) return std::wstring();

    int need = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.c_str(),
                                   static_cast<int>(text.size()), nullptr, 0);
    if (need <= 0) return std::wstring();

    std::wstring out(static_cast<size_t>(need), L'\0');
    int got = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.c_str(),
                                  static_cast<int>(text.size()), &out[0], need);
    if (got <= 0) return std::wstring();

    out.resize(static_cast<size_t>(got));
    return out;
}

bool ends_with_wav(const std::wstring& path)
{
    if (path.size() < 4) return false;
    return _wcsicmp(path.c_str() + path.size() - 4, L".wav") == 0;
}

// PlaySound 只认 WAV(其余格式系统会按扩展名猜, 容易放出噪声/静默失败), 所以
// 扩展名、存在性、类型、大小都在这里先卡住, 失败只记日志。
bool play_prompt_sound(const std::wstring& path)
{
    if (!ends_with_wav(path)) return false;

    WIN32_FILE_ATTRIBUTE_DATA attrs{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attrs)) return false;
    if ((attrs.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) return false;

    long long size = (static_cast<long long>(attrs.nFileSizeHigh) << 32) | attrs.nFileSizeLow;
    if (size <= 0 || size > kMaxSoundBytes) return false;

    // SND_ASYNC: 立刻返回, 不占监听线程; 新请求会打断上一段, 不会叠音。
    // SND_NODEFAULT: 失败时不要用系统默认提示音顶替。
    return PlaySoundW(path.c_str(), nullptr, SND_FILENAME | SND_ASYNC | SND_NODEFAULT) != FALSE;
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
    if (game == nullptr) return false;

    // 最小化: 用户看不见窗口, 即使系统仍把它算作 foreground, 也要恢复并提醒。
    if (IsIconic(game)) return false;

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

// 处理一条已解析的请求(供文件轮询与 UDP 共用, 便于日志口径一致)。
void handle_focus_request(const std::string& text)
{
    // 初始的 0 / 空白 / 乱码: 不是请求。
    cesium::windowing::FocusRequest request = cesium::windowing::parse_focus_request(text);
    if (!request.valid()) return;

    HWND hwnd = find_game_window();
    const bool foreground = (hwnd != nullptr) && is_foreground_game(hwnd);

    // 提示音策略: 默认只在"窗口不在前台"时播放 —— 这时置前才是真正需要的提醒;
    // 请求里带 always=1 时, 即使已经在前台也每次播放一次。
    bool sound_played = false;
    if (request.has_sound() && (request.always_play || !foreground))
    {
        std::wstring path = utf8_to_wide(request.sound_path);
        if (path.empty())
            log_line("[windowctl] prompt sound path is not valid UTF-8, ignored");
        else if (play_prompt_sound(path))
            sound_played = true;
        else
            log_line(L"[windowctl] failed to play prompt sound (need PCM WAV, <= 16 MB): " + short_path(path));
    }

    std::string sound_note;
    if (sound_played)
        sound_note = " (+ prompt sound)";
    else if (request.has_sound() && foreground)
        sound_note = " (prompt sound skipped: foreground)";

    if (hwnd == nullptr)
    {
        log_line(std::string("[windowctl] game window not found, foreground skipped") + sound_note);
        return;
    }

    if (foreground)
    {
        log_line(std::string("[windowctl] game window already in foreground, foreground skipped") + sound_note);
        return;
    }

    if (bring_to_foreground(hwnd))
        log_line(std::string("[windowctl] brought game window to foreground") + sound_note);
    else
        log_line(std::string("[windowctl] system refused foreground switch, flashed taskbar") + sound_note);
}

DWORD WINAPI udp_thread(LPVOID)
{
    log_line(L"[windowctl] UDP channel ready · 127.0.0.1:" + std::to_wstring(g_bound_port) +
             L" · sound=wav");

    char recv_buf[kMaxDatagramBytes + 1];
    for (;;)
    {
        sockaddr_in client_addr{};
        int client_len = sizeof(client_addr);
        int n = recvfrom(g_socket, recv_buf, static_cast<int>(sizeof(recv_buf) - 1), 0,
                         (sockaddr*)&client_addr, &client_len);
        if (n <= 0)
        {
            // 超时(500ms)或套接字错误: 超时是常态, 静默继续; 其它错误也只记日志不退出。
            if (n < 0)
            {
                const int err = WSAGetLastError();
                if (err != WSAETIMEDOUT && err != WSAEWOULDBLOCK)
                    log_line(L"[windowctl] UDP recv failed (error " + std::to_wstring(err) + L"), ignored");
            }
            continue;
        }
        if (static_cast<size_t>(n) >= sizeof(recv_buf))
        {
            log_line("[windowctl] UDP datagram too large, ignored");
            continue;
        }
        // 只接受本机回环: 绑了 127.0.0.1 本来就不会收到局域网包, 再校验一遍防
        // 大机型多网卡的回环绕行, 也能让"端口被同机其它程序扫到"时静默丢弃。
        if (client_addr.sin_addr.s_addr != htonl(INADDR_LOOPBACK))
            continue;

        recv_buf[n] = '\0';
        handle_focus_request(std::string(recv_buf, static_cast<size_t>(n)));
    }
    return 0;
}

} // namespace

std::wstring windowctl_dir()
{
    // 遗留路径: 不再承载传输, 仅供环境变量 CESIUM_WINDOW_DIR 兼容。
    return loader_root() + L"\\window";
}

void windowctl_start(const std::wstring& dir)
{
    (void)dir; // 已切换到 UDP, 不再创建/读写 window 目录
    if (InterlockedExchange(&g_started, 1) != 0) return;

    WSADATA wsa_data{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa_data) != 0)
    {
        log_line("[windowctl] WSAStartup failed, foreground channel unavailable");
        return;
    }

    g_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (g_socket == INVALID_SOCKET)
    {
        log_line("[windowctl] UDP socket creation failed, foreground channel unavailable");
        return;
    }

    // 500ms 接收超时: 线程既能及时处理请求, 又不会在进程退出过程中永久卡在
    // recvfrom 上(本进程退出不需要清理, 但超时让调试 shell / 卸载路径可感知)。
    const DWORD timeout_ms = 500;
    setsockopt(g_socket, SOL_SOCKET, SO_RCVTIMEO,
               reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms));

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK); // 127.0.0.1
    addr.sin_port = htons(0); // 0 = 让系统分配临时端口(每进程唯一, 天然隔离)

    if (bind(g_socket, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0)
    {
        log_line("[windowctl] UDP bind on 127.0.0.1 failed, foreground channel unavailable");
        return;
    }

    sockaddr_in bound{};
    int bound_len = sizeof(bound);
    if (getsockname(g_socket, reinterpret_cast<sockaddr*>(&bound), &bound_len) == 0)
    {
        g_bound_port = ntohs(bound.sin_port);
    }
    if (g_bound_port <= 0)
    {
        log_line("[windowctl] UDP getsockname failed, foreground channel unavailable");
        return;
    }

    // 端口只走进程环境变量: 这样每个进程只看见自己的端口, 不存在共享文件竞争。
    SetEnvironmentVariableW(L"CESIUM_FOCUS_PORT", std::to_wstring(g_bound_port).c_str());

    HANDLE h = CreateThread(nullptr, 0, udp_thread, nullptr, 0, nullptr);
    if (h != nullptr)
    {
        CloseHandle(h);
        return;
    }

    log_line("[windowctl] UDP listener thread creation failed, foreground channel unavailable");
}
