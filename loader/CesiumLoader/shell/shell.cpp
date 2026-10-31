// shell.cpp - 加载器调试 shell(见 shell.h)
//
// 线程模型:
//   一个常驻线程负责"读控制台输入 -> 解析 -> 执行 -> 重画提示符"。
//   日志仍然由各后台线程随时写入控制台, 所以提示符必须在每次写入前后被擦掉/重画 ——
//   由 console 的写入钩子(pre/post)在**持有控制台写锁**的情况下调用, 见 on_pre_write。
//
// 安全边界:
//   - 命令只操作原生加载器状态与配置文件, 不调用 il2cpp / Unity(跨线程调用会炸);
//   - 写配置一律"临时文件 + 校验 + 替换", 并备份 .bak; 改写后解析失败就不写;
//   - exit 会结束游戏进程(用户明确要求), detach 只关 shell。
// 拆分后的实现: shell_prompt.cpp(提示符) / shell_lineedit.cpp(行编辑) /
// shell_commands.cpp(命令) / shell_config.cpp + shell_config_io.cpp(配置); 共享声明见 shell_internal.h。

#include "shell/shell.h"

#include "shell/shell_internal.h"

#include "platform/loader.h"
#include "shell/lineedit.h"

#include <windows.h>

#include <atomic>

namespace term = cesium::shell::term;

namespace cesium::shell::term
{

// shell 线程退出标志: 命令(detach/exit/关闭 debugShell)与输入循环共用。
std::atomic<bool> g_quit{false};

} // namespace cesium::shell::term

namespace
{

std::atomic<bool> g_running{false};
HANDLE g_thread = nullptr;
DWORD g_saved_in_mode = 0;
bool g_have_saved_mode = false;

// ---------- 行编辑与线程 ----------

void enter_raw_mode()
{
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    if (in == nullptr || in == INVALID_HANDLE_VALUE) return;
    if (!GetConsoleMode(in, &g_saved_in_mode)) return;
    g_have_saved_mode = true;

    DWORD mode = g_saved_in_mode;
    // 鼠标的两个模式位分工不同, 这里要**一个开一个关**:
    //   ENABLE_MOUSE_INPUT   = 把鼠标事件塞进输入缓冲区交给客户端。打开时 conhost 不再自己
    //                          滚窗口, 而本 shell 不处理鼠标事件 —— 表现为滚轮没反应。必须关。
    //   ENABLE_QUICK_EDIT_MODE = 控制台宿主自己处理鼠标(拖选/右键复制)。关掉就只能看不能选,
    //                          必须开, 否则没法复制日志。
    // QuickEdit 的固有行为: 有选区时控制台输出会暂停(和 cmd 一样), 取消选区/复制完即恢复。
    mode &= ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT |
              ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT);
    mode |= ENABLE_EXTENDED_FLAGS | ENABLE_QUICK_EDIT_MODE;
    SetConsoleMode(in, mode);
}

void restore_console_mode()
{
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    if (!g_have_saved_mode || in == nullptr || in == INVALID_HANDLE_VALUE) return;
    SetConsoleMode(in, g_saved_in_mode);
    g_have_saved_mode = false;
}

BOOL WINAPI ctrl_handler(DWORD type)
{
    // 输入模式里已经关掉 ENABLE_PROCESSED_INPUT, 正常情况下 Ctrl+C 只是按键事件;
    // 这里再兜一层: 万一别处把模式改回去, 也不至于把游戏进程直接打死。
    if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT) return TRUE;
    return FALSE;
}

DWORD WINAPI thread_proc(LPVOID)
{
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    if (in == nullptr || in == INVALID_HANDLE_VALUE)
    {
        g_running = false;
        return 1;
    }

    enter_raw_mode();
    SetConsoleTitleW(L"CesiumLoader Debug Shell");
    SetConsoleCtrlHandler(ctrl_handler, TRUE);
    term::install_write_hooks();

    // 钩子注册后可能已经有一行日志把提示符画出来了(见 on_post_write), 这里只在没有时补画,
    // 否则会在同一行写出第二个提示符。
    term::draw_prompt_if_hidden();

    while (!term::g_quit)
    {
        INPUT_RECORD record{};
        DWORD count = 0;
        if (!ReadConsoleInputW(in, &record, 1, &count))
        {
            Sleep(30);
            continue;
        }
        if (record.EventType != KEY_EVENT || !record.Event.KeyEvent.bKeyDown) continue;
        term::handle_key(record.Event.KeyEvent);
    }

    term::g_suppress_redraw = true;
    // 退出前把提示符擦干净(detach 之后不该还留一个空提示符)。
    term::erase_prompt_before_exit();

    term::uninstall_write_hooks();
    SetConsoleTitleW(L"CesiumLoader Console");
    restore_console_mode();
    SetConsoleCtrlHandler(ctrl_handler, FALSE);
    term::raw_write_locked(L"\r\n");
    g_running = false;
    return 0;
}

} // namespace

namespace cesium::shell
{

bool shell_start()
{
    if (g_running.exchange(true)) return true;
    if (!console_available())
    {
        g_running = false;
        return false;
    }

    InitializeCriticalSection(&term::g_line_cs);
    term::g_line_cs_ready = true;
    term::g_quit = false;
    term::g_suppress_redraw = false;
    term::g_history_pos = 0;
    term::g_history_draft.clear();
    term::g_kill.clear();
    term::g_prompt_visible = false;
    term::g_prompt_end_valid = false;
    term::g_prompt_scroll = 0;
    lineedit::set_text(term::g_line, std::wstring());

    g_thread = CreateThread(nullptr, 0, thread_proc, nullptr, 0, nullptr);
    if (g_thread == nullptr)
    {
        g_running = false;
        return false;
    }
    CloseHandle(g_thread);
    g_thread = nullptr;
    return true;
}

bool shell_running()
{
    return g_running.load();
}

void shell_request_stop()
{
    term::g_quit = true;
}

} // namespace cesium::shell
