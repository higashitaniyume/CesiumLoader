// test_lineedit.cpp - 调试 shell 的行编辑与补全(纯逻辑)
//
// 覆盖: 码点移动/删除(代理对)、按词移动与剪切、补全的唯一候选/公共前缀/列候选判定。
// 屏幕上的光标定位与提示符重绘属于 Win32, 由联调台(build-harness.ps1)人工核对。
//
// 运行: pwsh -File tests\native\run-native-tests.ps1

#include "../../loader/CesiumLoader/shell/lineedit.h"

#include <iostream>
#include <string>
#include <vector>

namespace
{
int g_total = 0;
int g_fail = 0;

void check(const std::string& name, bool ok, const std::string& detail = "")
{
    ++g_total;
    if (!ok)
    {
        ++g_fail;
        std::cout << "  [FAIL] " << name << (detail.empty() ? "" : ": " + detail) << "\n";
    }
}

using namespace cesium::shell::lineedit;

std::string narrow(const std::wstring& text)
{
    std::string out;
    for (wchar_t c : text) out += (c < 0x80) ? static_cast<char>(c) : '?';
    return out;
}

void check_state(const std::string& name, const State& st, const std::wstring& text, size_t cursor)
{
    if (st.text != text || st.cursor != cursor)
        check(name, false, "文本=[" + narrow(st.text) + "] 光标=" + std::to_string(st.cursor) +
                               " (期望 [" + narrow(text) + "] " + std::to_string(cursor) + ")");
    else
        check(name, true);
}

State make(const std::wstring& text, size_t cursor)
{
    State st;
    st.text = text;
    st.cursor = cursor;
    return st;
}

void test_movement()
{
    std::cout << "== 光标移动/编辑 ==\n";

    // "ab中文" = a b 中 文; 光标停在中文前
    State st = make(L"ab\x4E2D\x6587", 2);
    move_right(st);
    check("move_right_cjk", st.cursor == 3, "光标=" + std::to_string(st.cursor));
    move_left(st);
    check("move_left_cjk", st.cursor == 2);

    // 代理对(emoji)算一个码点
    State emoji = make(L"a\xD83D\xDE00z", 1);
    move_right(emoji);
    check("move_right_surrogate", emoji.cursor == 3, "光标=" + std::to_string(emoji.cursor));
    backspace(emoji);
    check_state("backspace_surrogate", emoji, L"az", 1);

    State del = make(L"a\x4E2Dz", 1);
    delete_forward(del);
    check_state("delete_cjk", del, L"az", 1);

    State insert_mid = make(L"set X", 4);
    insert(insert_mid, L"abc");
    check_state("insert_mid", insert_mid, L"set abcX", 7);

    State home = make(L"status", 3);
    move_home(home);
    check("home", home.cursor == 0);
    move_end(home);
    check("end", home.cursor == 6);

    // 按空白分词: 路径里的反斜杠/冒号不断词
    State word = make(L"set loader.x C:\\tmp\\a.wav", 25);
    move_word_left(word);
    check("word_left", word.cursor == 13, "光标=" + std::to_string(word.cursor));
    move_word_right(word);
    check("word_right", word.cursor == 25, "光标=" + std::to_string(word.cursor));
}

void test_kill()
{
    std::cout << "== 剪切 ==\n";

    State to_end = make(L"set loader.x true", 11);
    const std::wstring cut = kill_to_end(to_end);
    check_state("kill_to_end", to_end, L"set loader.", 11);
    check("kill_to_end_returns", cut == L"x true", narrow(cut));

    State to_start = make(L"set loader.x true", 12);
    const std::wstring head = kill_to_start(to_start);
    check_state("kill_to_start", to_start, L" true", 0);
    check("kill_to_start_returns", head == L"set loader.x", narrow(head));

    State word = make(L"set loader.x true", 17);
    const std::wstring word_cut = kill_word(word);
    check_state("kill_word", word, L"set loader.x ", 13);
    check("kill_word_returns", word_cut == L"true", narrow(word_cut));

    State forward = make(L"get loader.a true", 4);
    const std::wstring forward_cut = kill_word_forward(forward);
    check_state("kill_word_forward", forward, L"get  true", 4);
    check("kill_word_forward_returns", forward_cut == L"loader.a", narrow(forward_cut));
}

void test_completion()
{
    std::cout << "== 补全 ==\n";

    const std::vector<std::wstring> commands = { L"exit", L"get", L"help", L"set", L"status" };

    // 唯一候选: 补成 "status "
    State unique = make(L"stat", 4);
    Completion one = complete(unique, 0, 4, commands);
    check_state("complete_unique", unique, L"status ", 7);
    check("complete_unique_changed", one.changed && !one.list);

    // 多候选但有公共前缀: status 与 stat 之外没有; 用 s 开头的 set/status 试公共前缀 "s"
    State multi = make(L"s", 1);
    Completion amb = complete(multi, 0, 1, commands);
    check("complete_ambiguous", amb.list && !amb.changed, "list=" + std::to_string(amb.list));
    check("complete_ambiguous_matches", amb.matches.size() == 2, std::to_string(amb.matches.size()));

    // 公共前缀能推进: loader.a / loader.ab 以 "loader.a" 键入时应列出
    const std::vector<std::wstring> keys = { L"loader.aaa", L"loader.aab", L"loader.b" };
    State key = make(L"loader.aa", 9);
    Completion prefix = complete(key, 0, 9, keys);
    check("complete_prefix_progress", !prefix.changed && prefix.list);

    // 大小写不敏感匹配, 插入候选本身的写法
    State upper = make(L"STAT", 4);
    Completion ci = complete(upper, 0, 4, commands);
    check_state("complete_case_insensitive", upper, L"status ", 7);
    (void)ci;

    // 无匹配: 不动
    State none = make(L"zzz", 3);
    Completion zero = complete(none, 0, 3, commands);
    check("complete_no_match", !zero.changed && !zero.list && none.text == L"zzz");

    // 光标在词中间: 只替换整个词, 后面的其余词保留
    State middle = make(L"st turn", 2);
    Completion mid = complete(middle, 0, 2, { L"status" });
    check_state("complete_mid_word", middle, L"status  turn", 7);
    (void)mid;

    // 公共前缀(忽略大小写)
    check("common_prefix", common_prefix({ L"Abc", L"aBD" }) == L"Ab", "");
    check("filter_prefix_ci", filter_prefix(commands, L"S").size() == 2, "");
}

} // namespace

int main()
{
    test_movement();
    test_kill();
    test_completion();

    std::cout << (g_fail == 0 ? "通过" : "失败") << ": " << (g_total - g_fail) << "/" << g_total << "\n";
    return g_fail == 0 ? 0 : 1;
}
