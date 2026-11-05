// windowtitle.cpp - 给游戏窗口标题追加 profile 名(说明见 windowtitle.h)
//
// 线程模型与 windowctl 相同: 一个常驻后台线程, 随进程退出结束, 不需要清理逻辑。
// 只调用 GetWindowTextW / SetWindowTextW, 不激活窗口、不改窗口状态或焦点。

#include "window/windowtitle.h"

#include "platform/loader.h"

#include <windows.h>

#include <cwchar>
#include <string>

namespace
{

// 后缀格式: " · <profile 名>"(如 "吉星派对 · P2")。
const wchar_t* const kSuffixSeparator = L" \u00B7 ";

// 窗口还没出现时的重试间隔; 出现后的标题检查间隔(游戏很少改标题, 2 秒足够)。
const DWORD kFindRetryMs = 500;
const DWORD kWatchMs = 2000;

// 窗口标题上限: Windows 标题一般远小于这个长度, 超出部分本来也会被截断。
const int kTitleMax = 512;

volatile LONG g_started = 0;
std::wstring g_suffix;

// 同进程里还有加载器控制台、输入法/文本服务等窗口, 它们不能当作游戏窗口。
bool excluded_class(const wchar_t* cls)
{
    if (cls == nullptr || cls[0] == L'\0') return true;
    if (_wcsicmp(cls, L"ConsoleWindowClass") == 0) return true;
    if (_wcsicmp(cls, L"PseudoConsoleWindow") == 0) return true;
    if (wcsstr(cls, L"IME") != nullptr) return true;
    if (wcsstr(cls, L"MSCTF") != nullptr) return true;
    return false;
}

struct FindWindowContext
{
    HWND main;
    HWND fallback;
    LONG fallback_area;
};

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

    // 兜底: 取同进程里面积最大的可见顶层窗口(排除控制台等)。
    if (GetWindowTextLengthW(hwnd) > 0)
    {
        RECT r{};
        if (GetWindowRect(hwnd, &r))
        {
            const LONG w = r.right - r.left;
            const LONG h = r.bottom - r.top;
            const LONG area = (w > 0 && h > 0) ? w * h : 0;
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

std::wstring window_title(HWND hwnd)
{
    wchar_t buf[kTitleMax] = {};
    const int len = GetWindowTextW(hwnd, buf, kTitleMax);
    if (len <= 0) return std::wstring();
    return std::wstring(buf, static_cast<size_t>(len));
}

bool ends_with(const std::wstring& text, const std::wstring& suffix)
{
    return text.size() >= suffix.size() && text.compare(text.size() - suffix.size(), suffix.size(), suffix) == 0;
}

DWORD WINAPI watch_thread(LPVOID)
{
    log_line(L"[windowtitle] game window title will carry the profile name: \"... " + g_suffix + L"\"");

    HWND hwnd = nullptr;
    bool logged = false;

    for (;;)
    {
        if (hwnd == nullptr || !IsWindow(hwnd))
            hwnd = find_game_window();

        if (hwnd == nullptr)
        {
            Sleep(kFindRetryMs);
            continue;
        }

        const std::wstring title = window_title(hwnd);
        // 游戏可能在我们设置过之后又把标题重置回原样: 每次都基于**当前**标题判断,
        // 只有还没带上后缀时才写, 因此不会叠成两个 " · P2 · P2"。
        if (!title.empty() && !ends_with(title, g_suffix))
        {
            const std::wstring desired = title + g_suffix;
            if (SetWindowTextW(hwnd, desired.c_str()))
            {
                if (!logged)
                {
                    log_line(L"[windowtitle] title is now \"" + desired + L"\"");
                    logged = true;
                }
            }
            else if (!logged)
            {
                log_line("[windowtitle] failed to set the game window title (error " +
                         std::to_string(GetLastError()) + ")");
            }
        }

        Sleep(kWatchMs);
    }
    return 0;
}

} // namespace

namespace cesium::windowing
{

void windowtitle_start(const std::wstring& profile_name)
{
    if (profile_name.empty()) return;
    if (InterlockedExchange(&g_started, 1) != 0) return;

    g_suffix = kSuffixSeparator + profile_name;

    HANDLE h = CreateThread(nullptr, 0, watch_thread, nullptr, 0, nullptr);
    if (h != nullptr)
    {
        CloseHandle(h);
        return;
    }

    log_line("[windowtitle] cannot start the title thread; the window title stays unchanged");
}

} // namespace cesium::windowing
