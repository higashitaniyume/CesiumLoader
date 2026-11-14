// watchdog.cpp - 主线程停滞看门狗(见 watchdog.h)
//
// 计时只用 GetSystemTimeAsFileTime: 变速引擎 inline hook 了 GetTickCount /
// GetTickCount64 / timeGetTime / QueryPerformanceCounter(见 speed/speedhack.h),
// 用被缩放过的时钟会让阈值随倍率漂移。
#include "runtime/watchdog.h"

#include "logging/logging.h"
#include "platform/loader.h"
#include "runtime/crashdump.h"

#include <windows.h>
#include <tlhelp32.h>

#include <cstdio>
#include <cstdint>
#include <string>

namespace
{

// 启动后的静默期: shader 编译 / Addressables 首次加载会把主线程卡住十几秒, 不能当卡死。
constexpr ULONGLONG kWarmupMs = 20000;

volatile LONG g_started = 0;

// 1970 起的毫秒数(只用来算差值)。刻意不用 GetTickCount64 —— 它被变速引擎 hook 了。
ULONGLONG now_ms()
{
    FILETIME ft{};
    GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER v{};
    v.LowPart = ft.dwLowDateTime;
    v.HighPart = ft.dwHighDateTime;
    return (v.QuadPart - 116444736000000000ULL) / 10000ULL;
}

std::string hex_text(unsigned long long value)
{
    char buf[32] = {};
    sprintf_s(buf, "0x%016llX", value);
    return buf;
}

// 主线程 = 进程内创建时间最早的线程。Toolhelp 只给 tid, 创建时间要 GetThreadTimes。
DWORD find_main_thread()
{
    const DWORD pid = GetCurrentProcessId();
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;

    DWORD best_tid = 0;
    ULARGE_INTEGER best_create{};
    best_create.QuadPart = ~0ULL;

    THREADENTRY32 te{};
    te.dwSize = sizeof(te);
    if (Thread32First(snap, &te))
    {
        do
        {
            if (te.th32OwnerProcessID != pid) continue;

            HANDLE h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, te.th32ThreadID);
            if (h == nullptr) continue;

            FILETIME created{}, exited{}, kernel{}, user{};
            if (GetThreadTimes(h, &created, &exited, &kernel, &user))
            {
                ULARGE_INTEGER v{};
                v.LowPart = created.dwLowDateTime;
                v.HighPart = created.dwHighDateTime;
                if (v.QuadPart < best_create.QuadPart)
                {
                    best_create = v;
                    best_tid = te.th32ThreadID;
                }
            }
            CloseHandle(h);
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
    return best_tid;
}

struct WindowSearch
{
    DWORD pid = 0;
    int visible = 0;
};

BOOL CALLBACK on_window(HWND hwnd, LPARAM param)
{
    auto* search = reinterpret_cast<WindowSearch*>(param);

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != search->pid) return TRUE;
    if (!IsWindowVisible(hwnd)) return TRUE;
    if (GetWindow(hwnd, GW_OWNER) != nullptr) return TRUE;   // 跳过子弹窗/工具窗

    ++search->visible;
    return FALSE;                                            // 找到一个就够
}

bool has_visible_window(DWORD pid)
{
    WindowSearch search;
    search.pid = pid;
    EnumWindows(on_window, reinterpret_cast<LPARAM>(&search));
    return search.visible > 0;
}

bool is_foreground(DWORD pid)
{
    HWND fg = GetForegroundWindow();
    if (fg == nullptr) return false;
    DWORD fg_pid = 0;
    GetWindowThreadProcessId(fg, &fg_pid);
    return fg_pid == pid;
}

DWORD WINAPI watch_thread(LPVOID param)
{
    const unsigned timeout_sec = static_cast<unsigned>(reinterpret_cast<uintptr_t>(param));
    const DWORD pid = GetCurrentProcessId();
    const ULONGLONG timeout_ms = static_cast<ULONGLONG>(timeout_sec) * 1000ULL;

    const DWORD main_tid = find_main_thread();
    if (main_tid == 0)
    {
        log_line("[watchdog] main thread not found; watchdog disabled");
        return 0;
    }

    HANDLE main_thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION,
                                    FALSE, main_tid);
    if (main_thread == nullptr)
    {
        log_line("[watchdog] cannot open main thread " + std::to_string(main_tid) + "; watchdog disabled");
        return 0;
    }

    // 有可见窗口时才做前台判定: 控制台/无窗口宿主没有"前台"可言, 直接看指针是否在动。
    // 启动时窗口可能还没建出来(UnityMain 之后才有), 所以每 5 秒复查一次, 而不是只判一次。
    bool windowed = has_visible_window(pid);
    ULONGLONG window_checked_at = 0;

    console_set_color(LOG_DIM);
    log_line("[watchdog] enabled: main thread " + std::to_string(main_tid) +
             " · stall threshold " + std::to_string(timeout_sec) + "s" +
             (windowed ? " · foreground gated" : " · no visible window yet"));
    console_set_color(LOG_DEFAULT);

    const ULONGLONG started = now_ms();
    ULONGLONG moved_at = started;
    ULONGLONG last_rip = 0;
    ULONGLONG last_rsp = 0;
    bool warned = false;
    bool dumped = false;

    for (;;)
    {
        Sleep(1000);
        const ULONGLONG now = now_ms();

        if (now - started < kWarmupMs)
        {
            moved_at = now;
            continue;
        }

        if (now - window_checked_at >= 5000)
        {
            windowed = has_visible_window(pid);
            window_checked_at = now;
        }

        // 窗口不在前台: 玩家可能切出去了, 主线程停住是正常的。重置停滞起点,
        // 避免切回来的一瞬间就被判定成卡死。
        if (windowed && !is_foreground(pid))
        {
            moved_at = now;
            if (warned)
            {
                log_line("[watchdog] game window left foreground; stall timer reset");
                warned = false;
            }
            continue;
        }

        // 调试器附加时"指针不动"是断点停住了, 不是卡死 —— 重置计时, 不落盘。
        if (IsDebuggerPresent())
        {
            moved_at = now;
            if (warned)
            {
                log_line("[watchdog] debugger attached; stall timer reset");
                warned = false;
            }
            continue;
        }

        if (SuspendThread(main_thread) == static_cast<DWORD>(-1))
        {
            log_line("[watchdog] SuspendThread failed (error " + std::to_string(GetLastError()) +
                     "); watchdog stopped");
            break;
        }

        CONTEXT ctx{};
        ctx.ContextFlags = CONTEXT_CONTROL;
        const BOOL got = GetThreadContext(main_thread, &ctx);
        ResumeThread(main_thread);              // 立刻恢复, 只借用几微秒
        if (!got) continue;

        if (ctx.Rip != last_rip || ctx.Rsp != last_rsp)
        {
            last_rip = ctx.Rip;
            last_rsp = ctx.Rsp;
            moved_at = now;
            if (warned)
            {
                log_line("[watchdog] main thread is moving again");
                warned = false;
            }
            continue;
        }

        const ULONGLONG stall_ms = now - moved_at;
        const ULONGLONG warn_ms = timeout_ms / 2;

        if (!warned && stall_ms >= warn_ms)
        {
            warned = true;
            console_set_color(LOG_YELLOW);
            log_line("[watchdog] main thread stalled " + std::to_string(stall_ms / 1000) + "s (rip " +
                     hex_text(ctx.Rip) + "); dump at " + std::to_string(timeout_sec) + "s");
            console_set_color(LOG_DEFAULT);
        }

        if (!dumped && stall_ms >= timeout_ms)
        {
            dumped = true;

            char note[192] = {};
            sprintf_s(note, "main thread rip/rsp unchanged for %.1fs (rip %s)",
                      (double)stall_ms / 1000.0, hex_text(ctx.Rip).c_str());

            const std::wstring path = cesium::crashdump::write_dump_now(
                "hang", "native-watchdog", note, static_cast<long long>(stall_ms),
                static_cast<unsigned long long>(ctx.Rip), static_cast<unsigned long long>(ctx.Rsp),
                static_cast<unsigned long>(main_tid));

            if (!path.empty())
            {
                console_set_color(LOG_RED);
                log_line(std::wstring(L"[watchdog] hang dump written: ") + short_path(path));
                console_set_color(LOG_DEFAULT);
            }
            else
            {
                log_line("[watchdog] hang dump failed (crash dump facility not ready)");
            }
        }
    }

    CloseHandle(main_thread);
    return 0;
}

} // namespace

namespace cesium::watchdog
{

void start(unsigned timeout_sec)
{
    if (timeout_sec < 10) timeout_sec = 10;
    if (timeout_sec > 600) timeout_sec = 600;

    if (InterlockedCompareExchange(&g_started, 1, 0) != 0) return;

    // 64 KB 栈足够(只有采样循环与几条日志), 不必占默认的 1 MB。
    HANDLE h = CreateThread(nullptr, 64 * 1024, watch_thread,
                            reinterpret_cast<LPVOID>(static_cast<uintptr_t>(timeout_sec)), 0, nullptr);
    if (h == nullptr)
    {
        log_line("[watchdog] CreateThread failed; watchdog disabled");
        return;
    }
    CloseHandle(h);
}

} // namespace cesium::watchdog
