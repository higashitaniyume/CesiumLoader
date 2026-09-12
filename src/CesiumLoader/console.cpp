// console.cpp - 控制台窗口与日志文件
//
// 游戏进程本身是 GUI(无控制台)。引导线程启动时 AllocConsole 分配一个
// 控制台窗口,loader 日志与 mod 的标准输出都写到这个窗口。
// 同时把日志追加写入 logs\cesium-loader.log。

#include "loader.h"

#include <io.h>
#include <fcntl.h>
#include <cstdio>

static HANDLE g_console_handle = INVALID_HANDLE_VALUE;
static std::wstring g_log_path;

// ---------- 路径 ----------

// loader_root = 游戏 exe 目录\AstralParty_ModLoader
// 通过 GetModuleFileNameW(NULL) 拿主模块(游戏 exe)路径定位游戏目录。
std::wstring loader_root()
{
    wchar_t buf[1024];
    DWORD n = GetModuleFileNameW(nullptr, buf, 1024);
    if (n == 0) return L".";
    std::wstring exe(buf, n);
    auto pos = exe.find_last_of(L"\\/");
    std::wstring game_dir = (pos == std::wstring::npos) ? L"." : exe.substr(0, pos);
    return game_dir + L"\\AstralParty_ModLoader";
}

std::wstring mods_dir()
{
    wchar_t buf[1024];
    DWORD n = GetEnvironmentVariableW(L"CESIUM_MODS_DIR", buf, 1024);
    if (n > 0 && n < 1024) return std::wstring(buf, n);
    return loader_root() + L"\\mods";
}

std::wstring sdk_dir()
{
    wchar_t buf[1024];
    DWORD n = GetEnvironmentVariableW(L"CESIUM_SDK_DIR", buf, 1024);
    if (n > 0 && n < 1024) return std::wstring(buf, n);
    return loader_root() + L"\\sdk";
}

std::wstring logs_dir()
{
    wchar_t buf[1024];
    DWORD n = GetEnvironmentVariableW(L"CESIUM_LOG_DIR", buf, 1024);
    if (n > 0 && n < 1024) return std::wstring(buf, n);
    return loader_root() + L"\\logs";
}

// ---------- 日志 ----------

static void ensure_log_path()
{
    if (!g_log_path.empty()) return;
    g_log_path = logs_dir() + L"\\cesium-loader.log";
    auto pos = g_log_path.find_last_of(L"\\/");
    if (pos != std::wstring::npos)
    {
        std::wstring dir = g_log_path.substr(0, pos);
        CreateDirectoryW(dir.c_str(), nullptr);
    }
}

void log_line(const char* msg)
{
    console_write(msg);
    ensure_log_path();
    if (g_log_path.empty()) return;
    HANDLE f = CreateFileW(g_log_path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(f, msg, (DWORD)strlen(msg), &written, nullptr);
    WriteFile(f, "\r\n", 2, &written, nullptr);
    CloseHandle(f);
}

// std::string 便捷重载(UTF-8 内容)
void log_line(const std::string& msg)
{
    log_line(msg.c_str());
}

void log_line(const std::wstring& msg)
{
    // 转 UTF-8 后走公共路径
    int len = WideCharToMultiByte(CP_UTF8, 0, msg.c_str(), (int)msg.size(), nullptr, 0, nullptr, nullptr);
    std::string utf8(len, '\0');
    WideCharToMultiByte(CP_UTF8, 0, msg.c_str(), (int)msg.size(), utf8.data(), len, nullptr, nullptr);
    log_line(utf8.c_str());
}

// ---------- 控制台 ----------

static HWND get_console_hwnd()
{
    return GetConsoleWindow();
}

void console_init()
{
    BOOL ok = AllocConsole();
    if (!ok)
    {
        // 可能已存在控制台或全屏独占模式; 记录但不致命
        log_line("[hijack] AllocConsole 返回 0 (可能已有控制台或全屏模式)");
    }
    SetConsoleTitleW(L"CesiumLoader Console");
    g_console_handle = GetStdHandle(STD_OUTPUT_HANDLE);
    log_line("[hijack] 控制台句柄: (AllocConsole=...)");

    // 把控制台窗口置顶,避免被游戏窗口完全遮挡
    HWND hwnd = get_console_hwnd();
    if (hwnd)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        log_line("[hijack] 控制台窗口置顶完成");
    }
    else
    {
        log_line("[hijack] 获取控制台窗口句柄失败(可能窗口被系统隐藏)");
    }
}

// 写一行文本到控制台(UTF-8 输入,内部转 UTF-16 用 WriteConsoleW)。
// 仅在 console_init 已调用后有效;未初始化时静默跳过(避免在 loader lock 下开窗口)。
void console_write(const char* utf8_msg)
{
    if (g_console_handle == INVALID_HANDLE_VALUE) return;
    int wlen = MultiByteToWideChar(CP_UTF8, 0, utf8_msg, -1, nullptr, 0);
    if (wlen <= 1) return;
    std::wstring wmsg(wlen - 1, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8_msg, -1, wmsg.data(), wlen);

    std::wstring line = wmsg + L"\n";
    DWORD written = 0;
    WriteConsoleW(g_console_handle, line.c_str(), (DWORD)line.size(), &written, nullptr);
}

void console_write_w(const wchar_t* msg, size_t len)
{
    if (g_console_handle == INVALID_HANDLE_VALUE || msg == nullptr || len == 0) return;
    std::wstring line(msg, len);
    line.push_back(L'\n');
    DWORD written = 0;
    WriteConsoleW(g_console_handle, line.c_str(), (DWORD)line.size(), &written, nullptr);
}

// ---------- 导出(供 C# mod 调用) ----------

// 签名:C 字符串(UTF-8),以 \0 结尾。
extern "C" __declspec(dllexport) void WINAPI ap_console_write(const char* msg)
{
    if (msg == nullptr) return;
    console_write(msg);
}

// 写一段 UTF-16 文本到控制台(长度按 wchar_t 字符数)。
extern "C" __declspec(dllexport) void WINAPI ap_console_write_w(const wchar_t* msg, size_t len)
{
    if (msg == nullptr || len == 0) return;
    console_write_w(msg, len);
}
