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

#include <cstring>
#include <string>
#include <vector>

#include "config/config.h"
#include "logging/logging.h"
#include "platform/loader.h"
#include "runtime/state.h"
#include "shell/shell.h"

namespace
{

// 一次按键: 字符键(VK=0) / 功能键(VK 有值, 字符为 0) / 带修饰键。
struct KeyStroke
{
    wchar_t ch;
    WORD vk;
    DWORD state;
};

KeyStroke char_key(wchar_t ch)
{
    if (ch == L'\r') return KeyStroke{ ch, VK_RETURN, 0 };
    if (ch == L'\t') return KeyStroke{ ch, VK_TAB, 0 };
    return KeyStroke{ ch, 0, 0 };
}

KeyStroke vk_key(WORD vk) { return KeyStroke{ 0, vk, 0 }; }

KeyStroke ctrl_char(wchar_t ch)
{
    return KeyStroke{ ch, 0, LEFT_CTRL_PRESSED };
}

std::vector<KeyStroke> text(const std::wstring& s)
{
    std::vector<KeyStroke> out;
    out.reserve(s.size());
    for (wchar_t ch : s) out.push_back(char_key(ch));
    return out;
}

void inject(const std::vector<KeyStroke>& keys)
{
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    if (in == nullptr || in == INVALID_HANDLE_VALUE) return;

    std::vector<INPUT_RECORD> records;
    records.reserve(keys.size() * 2);
    for (const KeyStroke& key : keys)
    {
        INPUT_RECORD down{};
        down.EventType = KEY_EVENT;
        down.Event.KeyEvent.bKeyDown = TRUE;
        down.Event.KeyEvent.wRepeatCount = 1;
        down.Event.KeyEvent.uChar.UnicodeChar = key.ch;
        down.Event.KeyEvent.wVirtualKeyCode = key.vk;
        down.Event.KeyEvent.dwControlKeyState = key.state;
        records.push_back(down);

        INPUT_RECORD up = down;
        up.Event.KeyEvent.bKeyDown = FALSE;
        records.push_back(up);
    }

    DWORD written = 0;
    WriteConsoleInputW(in, records.data(), (DWORD)records.size(), &written);
}

void inject(const std::wstring& s) { inject(text(s)); }

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

int main(int argc, char** argv)
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

    // --hold: 只把 shell 跑起来、不注入脚本, 供外部进程 attach 控制台后手动注入验证。
    if (argc > 1 && strcmp(argv[1], "--hold") == 0)
    {
        Sleep(25000);
        return 0;
    }

    // 用 exe 所在目录, 不依赖启动时的工作目录(Start-Process 的 cwd 不受控)。
    wchar_t self[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    std::wstring screen(self);
    const size_t slash = screen.find_last_of(L"\\/");
    if (slash != std::wstring::npos) screen = screen.substr(0, slash);
    screen += L"\\shell-screen.txt";
    DeleteFileW(screen.c_str());

    struct Step
    {
        std::vector<KeyStroke> keys;
        std::wstring label;
        bool clearBefore;
    };

    std::vector<Step> script;
    auto add = [&script](std::wstring label, std::vector<KeyStroke> keys, bool clearBefore)
    {
        script.push_back(Step{ std::move(keys), std::move(label), clearBefore });
    };

    add(L"help", text(L"help\r"), true);
    add(L"help set", text(L"help set\r"), true);
    add(L"set --help", text(L"set --help\r"), true);
    add(L"status", text(L"status\r"), true);
    add(L"ls TurnFocus", text(L"ls TurnFocus\r"), true);
    add(L"set TurnFocusMod.cooldownSeconds 3", text(L"set TurnFocusMod.cooldownSeconds 3\r"), true);
    add(L"set loader.consoleTopmost true", text(L"set loader.consoleTopmost true\r"), true);
    add(L"set loader.speedhackBaseSpeed 0.5", text(L"set loader.speedhackBaseSpeed 0.5\r"), true);
    add(L"set loader.domainTimeoutSec 45", text(L"set loader.domainTimeoutSec 45\r"), true);
    add(L"get loader.domainTimeoutSec", text(L"get loader.domainTimeoutSec\r"), true);
    add(L"mods", text(L"mods\r"), true);
    add(L"env", text(L"env\r"), true);

    // ---- 行编辑: 每条都只靠按键完成, 期望结果写在 label 里 ----
    {
        // 打错中间一个字符: 左移到错字后面, 退格删掉, 回车 —— 期望 consoleTopmost = true
        std::vector<KeyStroke> keys = text(L"set loader.consoleTopmost truXe");
        keys.push_back(vk_key(VK_LEFT));
        keys.push_back(vk_key(VK_BACK));
        keys.push_back(vk_key(VK_RETURN));
        add(L"编辑: ← + 退格修正中间字符 → true", std::move(keys), true);
    }
    {
        // 行首多打一个 X: Ctrl+A 回行首, Delete 删掉 —— 期望 consoleTopmost = false
        std::vector<KeyStroke> keys = text(L"Xset loader.consoleTopmost false");
        keys.push_back(ctrl_char(1));
        keys.push_back(vk_key(VK_DELETE));
        keys.push_back(vk_key(VK_RETURN));
        add(L"编辑: Ctrl+A + Delete 删行首 → false", std::move(keys), true);
    }
    {
        // Ctrl+W 删掉末尾多打的词 —— 期望 consoleTopmost = true
        std::vector<KeyStroke> keys = text(L"set loader.consoleTopmost true extra");
        keys.push_back(ctrl_char(23));
        keys.push_back(vk_key(VK_RETURN));
        add(L"编辑: Ctrl+W 删末词 → true", std::move(keys), true);
    }
    {
        // 光标插在中间输入: 先打 "et", Home 回行首, 再打 g —— 期望执行 get 的用法提示
        std::vector<KeyStroke> keys = text(L"et");
        keys.push_back(vk_key(VK_HOME));
        keys.push_back(char_key(L'g'));
        keys.push_back(vk_key(VK_RETURN));
        add(L"编辑: Home + 中间插入 → get", std::move(keys), true);
    }
    {
        // 历史草稿: 打一半翻上一条, 再翻回最新 —— 期望提示符里还是 "sta"
        std::vector<KeyStroke> keys = text(L"sta");
        keys.push_back(vk_key(VK_UP));
        keys.push_back(vk_key(VK_DOWN));
        add(L"历史: ↑↓ 后恢复正在编辑的内容", std::move(keys), true);
    }

    // ---- Tab 补全 ----
    {
        std::vector<KeyStroke> keys = text(L"statu");
        keys.push_back(vk_key(VK_TAB));
        add(L"补全: 命令名 → status", std::move(keys), true);
    }
    {
        std::vector<KeyStroke> keys = text(L"get loader.consoleT");
        keys.push_back(vk_key(VK_TAB));
        add(L"补全: 配置键 → loader.consoleTopmost", std::move(keys), true);
    }
    {
        std::vector<KeyStroke> keys = text(L"s");
        keys.push_back(vk_key(VK_TAB));
        add(L"补全: 多候选列清单(set/status)", std::move(keys), true);
    }
    {
        std::vector<KeyStroke> keys = text(L"ls Turn");
        keys.push_back(vk_key(VK_TAB));
        keys.push_back(vk_key(VK_TAB));
        add(L"补全: 配置键多候选 → TurnFocusMod.", std::move(keys), true);
    }

    add(L"paste Ctrl+V(不回车)", text(L"\x16"), true);
    add(L"回车执行粘贴内容", text(L"\r"), false);   // 不能先 clear, 否则会粘到上一行后面
    add(L"detach", text(L"detach\r"), true);

    for (const Step& step : script)
    {
        // 先清屏, 每次只抓这一条命令的结果(控制台的可读区域有限, 滚动历史读不到)。
        // 有的步骤故意不回车(补全/历史), 输入行里还留着字; 先 Esc 清空再 clear,
        // 否则 "clear" 会被接在残留内容后面。
        if (step.clearBefore)
        {
            std::vector<KeyStroke> reset = { vk_key(VK_ESCAPE) };
            const std::vector<KeyStroke> clear = text(L"clear\r");
            reset.insert(reset.end(), clear.begin(), clear.end());
            inject(reset);
            Sleep(250);
        }
        inject(step.keys);
        Sleep(650);
        dump_screen(screen, true, step.label);
    }

    (void)argc;
    return 0;
}
