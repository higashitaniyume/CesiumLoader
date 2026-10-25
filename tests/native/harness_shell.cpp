// harness_shell.cpp - 调试 shell 的离线联调台(一次性验证工具, 不进发布包)
//
// 为什么需要它:
//   shell 的纯逻辑有单测, 但"提示符 + 日志交错重绘 + 命令分发 + 真正写配置文件"这一整条
//   只有在真控制台里跑才看得出来。这个程序链接**加载器自己的** console/shell/config 源码,
//   开一个控制台, 启动 shell, 然后往自己的输入缓冲区注入一串命令, 最后把整个屏幕
//   抓成文本, 供人工核对。
//
// 用法: 由 build-harness.ps1 编译并运行; 输出 out\shell-screen.txt 与 out\AstralParty_ModLoader\ 下的配置。

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <string>
#include <vector>

#include "config/config.h"
#include "logging/logging.h"
#include "platform/loader.h"
#include "runtime/state.h"
#include "shell/shell.h"

namespace
{

void inject(const std::wstring& text)
{
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    if (in == nullptr || in == INVALID_HANDLE_VALUE) return;

    std::vector<INPUT_RECORD> records;
    for (wchar_t ch : text)
    {
        INPUT_RECORD down{};
        down.EventType = KEY_EVENT;
        down.Event.KeyEvent.bKeyDown = TRUE;
        down.Event.KeyEvent.wRepeatCount = 1;
        down.Event.KeyEvent.uChar.UnicodeChar = ch;
        down.Event.KeyEvent.wVirtualKeyCode = (ch == L'\r') ? VK_RETURN : 0;
        records.push_back(down);

        INPUT_RECORD up = down;
        up.Event.KeyEvent.bKeyDown = FALSE;
        records.push_back(up);
    }

    DWORD written = 0;
    WriteConsoleInputW(in, records.data(), (DWORD)records.size(), &written);
}

void dump_screen(const std::wstring& path, bool append, const std::wstring& label)
{
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (out == nullptr || out == INVALID_HANDLE_VALUE || !GetConsoleScreenBufferInfo(out, &info)) return;

    const int width = info.dwSize.X;
    const int height = info.dwSize.Y;
    std::vector<wchar_t> row(static_cast<size_t>(width), L'\0');

    std::wstring text;
    if (!label.empty())
    {
        text += L"\r\n===== ";
        text += label;
        text += L" =====\r\n";
    }

    int last_non_empty = 0;
    std::vector<std::wstring> lines;
    lines.reserve(static_cast<size_t>(height));

    for (int y = 0; y < height; ++y)
    {
        DWORD read = 0;
        if (!ReadConsoleOutputCharacterW(out, row.data(), (DWORD)width, {0, (SHORT)y}, &read)) break;
        std::wstring line(row.data(), read);
        while (!line.empty() && (line.back() == L' ' || line.back() == L'\0')) line.pop_back();
        if (!line.empty()) last_non_empty = y;
        lines.push_back(line);
    }

    for (int y = 0; y <= last_non_empty && y < (int)lines.size(); ++y)
    {
        text += lines[y];
        text += L"\r\n";
    }

    const int len = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), nullptr, 0, nullptr, nullptr);
    std::string utf8(static_cast<size_t>(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), utf8.data(), len, nullptr, nullptr);

    HANDLE file = CreateFileW(path.c_str(), append ? FILE_APPEND_DATA : GENERIC_WRITE,
                              FILE_SHARE_READ, nullptr, append ? OPEN_ALWAYS : CREATE_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(file, utf8.data(), (DWORD)utf8.size(), &written, nullptr);
    CloseHandle(file);
}

} // namespace

int main(int argc, char**)
{
    // 运行期快照: 让 status/mods 有内容可显示。
    cesium::runtime::mark_started();
    LoaderConfig cfg = load_config(config_path());
    cesium::runtime::set_config(cfg);
    cesium::runtime::set_forward_enabled(false);
    cesium::runtime::set_versions("3.3.0", "2022.3.62f3");
    wchar_t exe[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    cesium::runtime::set_exe_path(exe);
    cesium::runtime::set_mods({
        { "TurnFocusMod", "1.0.0", "ok  5ms", true, true },
        { "CombatOddsMod", "2.2.0", "ok  9ms", true, true },
        { "BrokenMod", "0.1.0", "load failed: boom", true, false },
        { "DisabledMod", "-", "disabled", false, false },
    });

    console_init(false);

    // 新分配的控制台缓冲区可能只有窗口那么高, 早先的输出会被滚掉; 调高缓冲区再跑,
    // 这样抓屏能拿到完整会话(联调台专用, 加载器本身不动控制台尺寸)。
    {
        HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
        CONSOLE_SCREEN_BUFFER_INFO info{};
        if (out && out != INVALID_HANDLE_VALUE && GetConsoleScreenBufferInfo(out, &info) && info.dwSize.Y < 300)
        {
            COORD size{ info.dwSize.X, 300 };
            SetConsoleScreenBufferSize(out, size);
        }
    }

    if (!cesium::shell::shell_start())
    {
        log_line("[harness] shell_start failed");
        return 1;
    }

    Sleep(600);

    // 用 exe 所在目录, 不依赖启动时的工作目录(Start-Process 的 cwd 不受控)。
    wchar_t self[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    std::wstring screen(self);
    const size_t slash = screen.find_last_of(L"\\/");
    if (slash != std::wstring::npos) screen = screen.substr(0, slash);
    screen += L"\\shell-screen.txt";
    DeleteFileW(screen.c_str());

    const wchar_t* script[] = {
        L"help set\r",
        L"set --help\r",
        L"status\r",
        L"ls TurnFocus\r",
        L"set TurnFocusMod.cooldownSeconds 3\r",
        L"set loader.consoleTopmost true\r",
        L"set loader.speedhackBaseSpeed 0.5\r",
        L"set loader.domainTimeoutSec 45\r",
        L"get loader.domainTimeoutSec\r",
        L"mods\r",
        L"env\r",
        L"detach\r",
    };

    for (const wchar_t* line : script)
    {
        // 先清屏, 每次只抓这一条命令的结果(控制台的可读区域有限, 滚动历史读不到)。
        inject(L"clear\r");
        Sleep(250);
        inject(line);
        Sleep(650);

        std::wstring label(line);
        while (!label.empty() && (label.back() == L'\r' || label.back() == L'\n')) label.pop_back();
        dump_screen(screen, true, label);
    }

    (void)argc;
    return 0;
}
