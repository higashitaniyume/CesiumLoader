// test_window_request.cpp - window\request.txt 解析的边界用例
//
// windowctl 的提示音路径和置前令牌共用一份请求文本, 解析必须对旧 mod(只有令牌)、
// 新 mod(令牌 + 路径)、以及各种畸形输入都稳定: 令牌非法 = 整条请求忽略;
// 路径非法 = 只丢提示音, 置前照常。这里只测纯解析(不碰 Win32), 播放逻辑由实机验证。
//
// 运行: powershell -File tests\native\run-native-tests.ps1

#include "../../loader/CesiumLoader/window/request.h"

#include <iostream>
#include <string>

namespace
{
int g_total = 0;
int g_fail = 0;

using cesium::windowing::FocusRequest;
using cesium::windowing::parse_focus_request;

void check(const std::string& name, bool ok, const std::string& detail)
{
    ++g_total;
    if (!ok)
    {
        ++g_fail;
        std::cout << "  [FAIL] " << name << ": " << detail << "\n";
    }
}

void check_request(const std::string& name, const std::string& text, bool expectValid,
                   const std::string& expectPath)
{
    FocusRequest req = parse_focus_request(text);

    if (req.valid() != expectValid)
    {
        check(name, false, std::string("valid 期望 ") + (expectValid ? "true" : "false") +
                           ", 实际 " + (req.valid() ? "true" : "false") +
                           " (token='" + req.token + "')");
        return;
    }
    if (req.sound_path != expectPath)
    {
        check(name, false, "sound_path 期望 '" + expectPath + "', 实际 '" + req.sound_path + "'");
        return;
    }
    check(name, true, "");
}
} // namespace

int main()
{
    std::cout << "== window request 解析 ==\n";

    const std::string wav = "C:\\Games\\Astral Party\\AstralParty_ModLoader\\mods\\TurnFocusMod\\sounds\\your-turn.wav";

    // 旧 mod: 只有令牌。必须继续可用, 且不带提示音。
    check_request("old_protocol_token_only", "123456789\r\n", true, "");
    check_request("token_without_newline", "123456789", true, "");
    check_request("token_leading_space", "   42\r\n", true, "");
    check_request("token_trailing_tab", "42\t\r\n", true, "");

    // 新 mod: 令牌 + 绝对路径。
    check_request("token_and_path_crlf", "99\r\n" + wav + "\r\n", true, wav);
    check_request("token_and_path_lf", "99\n" + wav + "\n", true, wav);
    check_request("token_and_path_cr", "99\r" + wav + "\r", true, wav);
    check_request("token_and_path_no_final_newline", "99\r\n" + wav, true, wav);
    check_request("path_with_spaces_trimmed", "99\r\n  " + wav + "  \r\n", true, wav);
    check_request("forward_slashes_path", "99\r\nC:/tmp/ding.wav\r\n", true, "C:/tmp/ding.wav");
    check_request("unc_path", "99\r\n\\\\server\\share\\ding.wav\r\n", true, "\\\\server\\share\\ding.wav");

    // UTF-8 中文路径按字节原样保留(加载器随后用 CP_UTF8 转宽字符)。
    const std::string chinese = "C:\\Users\\\xE5\xBC\xA0\xE4\xB8\x89\\sounds\\\xE6\x8F\x90\xE7\xA4\xBA\xE9\x9F\xB3.wav";
    check_request("utf8_path_preserved", "99\r\n" + chinese + "\r\n", true, chinese);

    // 令牌非法: 整条请求忽略(含加载器启动时写入的 0)。
    check_request("reset_zero", "0\r\n", false, "");
    check_request("reset_zero_with_path", "0\r\n" + wav + "\r\n", false, "");
    check_request("empty", "", false, "");
    check_request("only_whitespace", " \r\n\t\r\n", false, "");
    check_request("non_numeric_token", "abc\r\n" + wav + "\r\n", false, "");
    check_request("negative_token", "-1\r\n", false, "");
    check_request("token_too_long", std::string(65, '7') + "\r\n", false, "");

    // 路径非法: 只丢提示音, 置前照常。
    check_request("relative_path_dropped", "99\r\nsounds\\ding.wav\r\n", true, "");
    check_request("drive_relative_dropped", "99\r\nC:ding.wav\r\n", true, "");
    check_request("path_with_control_dropped", std::string("99\r\nC:\\a\\b") + '\x01' + ".wav\r\n", true, "");
    check_request("path_too_long_dropped", "99\r\nC:\\" + std::string(4200, 'a') + ".wav\r\n", true, "");
    check_request("empty_second_line", "99\r\n\r\n", true, "");

    // 多余行忽略, 不污染路径。
    check_request("extra_lines_ignored", "99\r\n" + wav + "\r\nfuture=1\r\n", true, wav);

    std::cout << "  用例: " << g_total << "  失败: " << g_fail << "\n";
    if (g_fail == 0) std::cout << "== 全部通过 ==\n";
    return g_fail == 0 ? 0 : 1;
}
