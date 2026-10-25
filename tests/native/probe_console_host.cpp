// probe_console_host.cpp - AllocConsole 宿主探针(一次性诊断, 不是单元测试)
//
// 目的: 回答"GUI 进程(游戏)调用 AllocConsole 时, 默认终端设置会不会被尊重"。
// 做法与游戏一致: 先 FreeConsole 清掉可能继承来的控制台, 再 AllocConsole.
//
// 判定依据(比"看窗口长什么样"可靠):
//   GetConsoleWindow() 返回的窗口属于**控制台宿主进程**。
//     conhost 托管  -> 窗口属于 conhost.exe, 且可见 (ConsoleWindowClass)
//     WT/ConPTY 托管 -> 窗口是隐藏的消息窗, 属于 OpenConsole.exe / WindowsTerminal.exe
//   再用 GetWindowThreadProcessId + QueryFullProcessImageName 取宿主 exe 名。
//
// 同时验证调试 shell 需要的两个前提:
//   1) WriteConsoleInputW 注入一行 -> ReadConsoleW 能读回(输入通道可用)
//   2) SetConsoleCtrlHandler 吞掉 Ctrl+C -> 进程不被杀(防止游戏被 Ctrl+C 打死)
//
// 结果写到 out\console-probe.txt (UTF-8), 同时打印到控制台。
// 编译: 见同目录 build-probe.ps1 (GUI 子系统, /SUBSYSTEM:WINDOWS)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>

#include <cstdarg>
#include <cstdio>
#include <string>
#include <vector>

namespace
{

const wchar_t* kOutPath = L"C:\\src\\Study\\astralparty\\modding\\msvc\\tests\\native\\out\\console-probe.txt";

void append_utf8(const std::wstring& text)
{
    const int len = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), nullptr, 0, nullptr, nullptr);
    std::string utf8(static_cast<size_t>(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), utf8.data(), len, nullptr, nullptr);

    HANDLE h = CreateFileW(kOutPath, FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(h, utf8.data(), (DWORD)utf8.size(), &written, nullptr);
    CloseHandle(h);
}

std::wstring wfmt(const wchar_t* fmt, ...)
{
    wchar_t buf[1024];
    va_list args;
    va_start(args, fmt);
    vswprintf_s(buf, 1024, fmt, args);
    va_end(args);
    return buf;
}

std::wstring image_name(DWORD pid)
{
    if (pid == 0) return L"(pid 0)";
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (h == nullptr) return wfmt(L"(open failed %lu)", GetLastError());
    wchar_t buf[MAX_PATH] = {};
    DWORD len = MAX_PATH;
    std::wstring name;
    if (QueryFullProcessImageNameW(h, 0, buf, &len))
    {
        name = buf;
        const size_t slash = name.find_last_of(L"\\/");
        if (slash != std::wstring::npos) name = name.substr(slash + 1);
    }
    else
    {
        name = wfmt(L"(query failed %lu)", GetLastError());
    }
    CloseHandle(h);
    return name;
}

std::wstring window_class(HWND hwnd)
{
    if (hwnd == nullptr) return L"(null)";
    wchar_t buf[256] = {};
    GetClassNameW(hwnd, buf, 256);
    return buf;
}

// 本进程的所有顶层窗口(含控制台那个), 用来判断窗口是真窗口还是 ConPTY 的占位窗。
std::wstring describe_own_windows()
{
    struct Ctx
    {
        DWORD pid;
        std::wstring text;
    } ctx{GetCurrentProcessId(), L""};

    EnumWindows([](HWND hwnd, LPARAM param) -> BOOL
    {
        Ctx* c = reinterpret_cast<Ctx*>(param);
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid != c->pid) return TRUE;

        wchar_t title[128] = {};
        GetWindowTextW(hwnd, title, 128);
        RECT rc{};
        GetWindowRect(hwnd, &rc);
        c->text += wfmt(L"  win hwnd=%p class=%s visible=%d rect=%ld,%ld %ldx%ld title='%s'\r\n",
                        (void*)hwnd, window_class(hwnd).c_str(), (int)IsWindowVisible(hwnd),
                        rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, title);
        return TRUE;
    }, reinterpret_cast<LPARAM>(&ctx));

    return ctx.text;
}

// 本进程的直接子进程。经典 conhost 模式: conhost.exe 是控制台客户端的子进程;
// ConPTY(终端托管)模式: 没有 conhost 子进程, 控制台服务器是 WT 那侧的 OpenConsole.exe。
std::wstring describe_children()
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return L"  (snapshot failed)\r\n";

    const DWORD self = GetCurrentProcessId();
    std::wstring text;
    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);
    if (Process32FirstW(snap, &pe))
    {
        do
        {
            if (pe.th32ParentProcessID == self && pe.th32ProcessID != self)
                text += wfmt(L"  child pid=%lu exe=%s\r\n", pe.th32ProcessID, pe.szExeFile);
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    if (text.empty()) text = L"  (no child processes)\r\n";
    return text;
}

std::wstring describe_hosts()
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return L"  (snapshot failed)\r\n";

    std::wstring text;
    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);
    if (Process32FirstW(snap, &pe))
    {
        do
        {
            const std::wstring name = pe.szExeFile;
            if (name == L"conhost.exe" || name == L"OpenConsole.exe" ||
                name == L"WindowsTerminal.exe" || name == L"WindowsTerminalPreview.exe")
            {
                text += wfmt(L"  pid=%lu ppid=%lu exe=%s\r\n",
                             pe.th32ProcessID, pe.th32ParentProcessID, pe.szExeFile);
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    return text.empty() ? L"  (none)\r\n" : text;
}

// conhost.exe 是本进程的子进程 => 经典控制台托管。
bool has_conhost_child()
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return false;

    const DWORD self = GetCurrentProcessId();
    bool found = false;
    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);
    if (Process32FirstW(snap, &pe))
    {
        do
        {
            if (pe.th32ParentProcessID == self && _wcsicmp(pe.szExeFile, L"conhost.exe") == 0)
            {
                found = true;
                break;
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    return found;
}

// ---------- 输入通道测试 ----------

struct ReadResult
{
    HANDLE done = nullptr;
    std::wstring line;
    DWORD chars = 0;
    BOOL ok = FALSE;
};

DWORD WINAPI read_thread(LPVOID param)
{
    ReadResult* r = static_cast<ReadResult*>(param);
    wchar_t buf[256] = {};
    r->ok = ReadConsoleW(GetStdHandle(STD_INPUT_HANDLE), buf, 255, &r->chars, nullptr);
    if (r->ok && r->chars > 0) r->line.assign(buf, r->chars);
    SetEvent(r->done);
    return 0;
}

// 往自己的输入缓冲区注入 "status\r", 模拟键盘输入。
bool inject_line()
{
    const wchar_t* text = L"status\r";
    std::vector<INPUT_RECORD> recs;
    for (const wchar_t* p = text; *p != L'\0'; ++p)
    {
        INPUT_RECORD down{};
        down.EventType = KEY_EVENT;
        down.Event.KeyEvent.bKeyDown = TRUE;
        down.Event.KeyEvent.wRepeatCount = 1;
        down.Event.KeyEvent.uChar.UnicodeChar = *p;
        down.Event.KeyEvent.wVirtualKeyCode = (*p == L'\r') ? VK_RETURN : 0;
        recs.push_back(down);

        INPUT_RECORD up = down;
        up.Event.KeyEvent.bKeyDown = FALSE;
        recs.push_back(up);
    }
    DWORD written = 0;
    return WriteConsoleInputW(GetStdHandle(STD_INPUT_HANDLE), recs.data(), (DWORD)recs.size(), &written) != FALSE;
}

// ---------- Ctrl+C 测试 ----------

volatile LONG g_ctrl_seen = 0;

BOOL WINAPI ctrl_handler(DWORD type)
{
    // 只吞 Ctrl+C / Ctrl+Break; 关窗口/注销/关机照旧交给默认处理。
    if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT)
    {
        InterlockedExchange(&g_ctrl_seen, 1);
        return TRUE;
    }
    return FALSE;
}

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    // 关键: 必须从一个**从来没有控制台**的进程里调 AllocConsole, 才等价于游戏
    // (Steam 启动的 GUI 进程没有控制台)。父进程(这里)有控制台时, 子进程会继承它;
    // 即使 FreeConsole 再 AllocConsole, 也可能因为进程的控制台状态已初始化而
    // 绕过宿主选择。所以先以 DETACHED_PROCESS 重新拉起自己。
    if (wcsstr(GetCommandLineW(), L"--child") == nullptr)
    {
        wchar_t self[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, self, MAX_PATH);
        std::wstring cmd = std::wstring(L"\"") + self + L"\" --child";

        STARTUPINFOW si{};
        si.cb = sizeof(si);
        PROCESS_INFORMATION pi{};
        if (!CreateProcessW(self, cmd.data(), nullptr, nullptr, FALSE, DETACHED_PROCESS, nullptr,
                            nullptr, &si, &pi))
        {
            append_utf8(wfmt(L"detach_spawn_failed=%lu\r\n", GetLastError()));
            return 1;
        }
        WaitForSingleObject(pi.hProcess, 20000);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return 0;
    }

    DeleteFileW(kOutPath);

    // 模拟游戏: 此时应当没有控制台(GUI 进程 + DETACHED_PROCESS)。
    const HWND hwnd_before = GetConsoleWindow();
    const BOOL had_console = FreeConsole();
    const BOOL alloc_ok = AllocConsole();
    const DWORD alloc_err = alloc_ok ? 0 : GetLastError();

    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    HWND hwnd = GetConsoleWindow();
    DWORD host_pid = 0;
    if (hwnd) GetWindowThreadProcessId(hwnd, &host_pid);
    const std::wstring host = image_name(host_pid);

    DWORD in_mode = 0, out_mode = 0;
    GetConsoleMode(in, &in_mode);
    GetConsoleMode(out, &out_mode);

    std::wstring report;
    report += wfmt(L"had_console_before=%d hwnd_before=%p\r\n", (int)had_console, (void*)hwnd_before);
    report += wfmt(L"alloc_console=%d err=%lu\r\n", (int)alloc_ok, alloc_err);
    report += wfmt(L"stdin=%p stdout=%p\r\n", (void*)in, (void*)out);
    report += wfmt(L"hwnd=%p class=%s visible=%d\r\n", (void*)hwnd, window_class(hwnd).c_str(),
                   hwnd ? (int)IsWindowVisible(hwnd) : -1);
    report += wfmt(L"host_pid=%lu host_exe=%s\r\n", host_pid, host.c_str());
    report += wfmt(L"in_mode=0x%08lX out_mode=0x%08lX\r\n", in_mode, out_mode);

    // 宿主判定: conhost.exe 作为子进程出现 = 经典托管(窗口属于 conhost);
    // 没有 conhost 子进程、但有 OpenConsole.exe = 已交给 Windows Terminal(ConPTY)。
    const bool conhost_child = has_conhost_child();
    const std::wstring children_text = describe_children();
    const std::wstring hosts_text = describe_hosts();

    const wchar_t* verdict = L"unknown";
    if (hwnd == nullptr)
        verdict = L"no_console";
    else if (conhost_child)
        verdict = L"CONHOST";
    else if (hosts_text.find(L"OpenConsole.exe") != std::wstring::npos)
        verdict = L"WINDOWS_TERMINAL";
    report += wfmt(L"verdict=%s\r\n", verdict);
    report += L"own_windows:\r\n" + describe_own_windows();
    report += L"children:\r\n" + children_text;
    report += L"hosts:\r\n" + hosts_text;

    append_utf8(report);

    // 输入通道: 注入一行再读回。
    ReadResult rr;
    rr.done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (inject_line())
    {
        HANDLE th = CreateThread(nullptr, 0, read_thread, &rr, 0, nullptr);
        const DWORD wait = WaitForSingleObject(rr.done, 3000);
        if (wait == WAIT_TIMEOUT)
            append_utf8(L"input_read=timeout\r\n");
        else
            append_utf8(wfmt(L"input_read=%d line=%s\r\n", (int)rr.ok, rr.line.c_str()));
        if (th) CloseHandle(th);
    }
    else
    {
        append_utf8(wfmt(L"input_inject=failed err=%lu\r\n", GetLastError()));
    }

    // Ctrl+C: 装 handler 后自己发一次, 进程必须活着。
    const BOOL handler_ok = SetConsoleCtrlHandler(ctrl_handler, TRUE);
    const BOOL sent = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
    Sleep(400);
    append_utf8(wfmt(L"ctrl_handler=%d ctrl_sent=%d ctrl_seen=%d alive_after_ctrl_c=%d\r\n",
                     (int)handler_ok, (int)sent, (int)InterlockedCompareExchange(&g_ctrl_seen, 0, 0), 1));

    // 留一点时间让人看到窗口; 也顺便测一下窗口标题。
    SetConsoleTitleW(L"Cesium console-host probe");
    Sleep(9000);
    return 0;
}

