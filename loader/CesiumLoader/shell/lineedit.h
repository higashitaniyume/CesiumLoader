// lineedit.h - 调试 shell 的行编辑与补全(纯逻辑, 可单测)
//
// 只处理字符串与光标下标, 不碰 Win32/控制台 —— 提示符重绘、按键分发在 shell.cpp。
// 行为对齐常见 shell(readline/cmd/PowerShell):
//   - 光标按**码点**移动, 不把代理对(emoji)劈成两半;
//   - 词边界按空白划分(路径里的反斜杠/冒号不是分隔符, 否则 C:\a\b 会被拆碎);
//   - 补全唯一候选时补一个空格; 候选不唯一时补公共前缀, 补不动才提示列候选。

#pragma once

#include <cstddef>
#include <string>
#include <vector>

namespace cesium::shell::lineedit
{

inline bool is_space(wchar_t c)
{
    return c == L' ' || c == L'\t';
}

// 光标左侧一个码点的起点(代理对算一个码点)。
inline size_t prev_boundary(const std::wstring& text, size_t cursor)
{
    if (cursor == 0 || cursor > text.size()) return 0;
    --cursor;
    if (cursor > 0 &&
        text[cursor] >= 0xDC00 && text[cursor] <= 0xDFFF &&
        text[cursor - 1] >= 0xD800 && text[cursor - 1] <= 0xDBFF)
        --cursor;
    return cursor;
}

// 光标右侧一个码点的终点。
inline size_t next_boundary(const std::wstring& text, size_t cursor)
{
    if (cursor >= text.size()) return text.size();
    const wchar_t c = text[cursor];
    if (c >= 0xD800 && c <= 0xDBFF && cursor + 1 < text.size() &&
        text[cursor + 1] >= 0xDC00 && text[cursor + 1] <= 0xDFFF)
        return cursor + 2;
    return cursor + 1;
}

struct State
{
    std::wstring text;
    size_t cursor = 0;
};

inline void set_text(State& st, const std::wstring& text)
{
    st.text = text;
    st.cursor = text.size();
}

inline void insert(State& st, const std::wstring& text)
{
    if (text.empty()) return;
    st.text.insert(st.cursor, text);
    st.cursor += text.size();
}

inline bool backspace(State& st)
{
    if (st.cursor == 0) return false;
    const size_t from = prev_boundary(st.text, st.cursor);
    st.text.erase(from, st.cursor - from);
    st.cursor = from;
    return true;
}

inline bool delete_forward(State& st)
{
    if (st.cursor >= st.text.size()) return false;
    const size_t to = next_boundary(st.text, st.cursor);
    st.text.erase(st.cursor, to - st.cursor);
    return true;
}

inline void move_left(State& st)
{
    st.cursor = prev_boundary(st.text, st.cursor);
}

inline void move_right(State& st)
{
    st.cursor = next_boundary(st.text, st.cursor);
}

inline void move_word_left(State& st)
{
    size_t i = st.cursor;
    while (i > 0 && is_space(st.text[i - 1])) --i;
    while (i > 0 && !is_space(st.text[i - 1])) --i;
    st.cursor = i;
}

inline void move_word_right(State& st)
{
    size_t i = st.cursor;
    while (i < st.text.size() && is_space(st.text[i])) ++i;
    while (i < st.text.size() && !is_space(st.text[i])) ++i;
    st.cursor = i;
}

inline void move_home(State& st) { st.cursor = 0; }
inline void move_end(State& st) { st.cursor = st.text.size(); }

// 删除并返回被删内容(Ctrl+K / Ctrl+U / Ctrl+W 的剪切语义, 供 Ctrl+Y 粘回)。
inline std::wstring kill_to_end(State& st)
{
    std::wstring cut = st.text.substr(st.cursor);
    st.text.erase(st.cursor);
    return cut;
}

inline std::wstring kill_to_start(State& st)
{
    std::wstring cut = st.text.substr(0, st.cursor);
    st.text.erase(0, st.cursor);
    st.cursor = 0;
    return cut;
}

inline std::wstring kill_word(State& st)
{
    const size_t to = st.cursor;
    move_word_left(st);
    std::wstring cut = st.text.substr(st.cursor, to - st.cursor);
    st.text.erase(st.cursor, to - st.cursor);
    return cut;
}

inline std::wstring kill_word_forward(State& st)
{
    const size_t from = st.cursor;
    move_word_right(st);
    const size_t to = st.cursor;
    std::wstring cut = st.text.substr(from, to - from);
    st.text.erase(from, to - from);
    st.cursor = from;
    return cut;
}

// ---------- 补全 ----------

inline wchar_t lower_ascii_w(wchar_t c)
{
    return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c - L'A' + L'a') : c;
}

inline bool starts_with_ci(const std::wstring& text, const std::wstring& prefix)
{
    if (text.size() < prefix.size()) return false;
    for (size_t i = 0; i < prefix.size(); ++i)
        if (lower_ascii_w(text[i]) != lower_ascii_w(prefix[i])) return false;
    return true;
}

// 公共前缀(忽略 ASCII 大小写; 返回值取第一个候选的写法)。
inline std::wstring common_prefix(const std::vector<std::wstring>& items)
{
    if (items.empty()) return std::wstring();
    std::wstring prefix = items.front();
    for (size_t k = 1; k < items.size() && !prefix.empty(); ++k)
    {
        size_t i = 0;
        while (i < prefix.size() && i < items[k].size() &&
               lower_ascii_w(prefix[i]) == lower_ascii_w(items[k][i]))
            ++i;
        prefix.resize(i);
    }
    return prefix;
}

inline std::vector<std::wstring> filter_prefix(const std::vector<std::wstring>& items,
                                              const std::wstring& prefix)
{
    std::vector<std::wstring> out;
    for (const std::wstring& item : items)
        if (starts_with_ci(item, prefix)) out.push_back(item);
    return out;
}

struct Completion
{
    bool changed = false;                 // 行内容被改写
    bool list = false;                    // 候选不唯一且补不动: 调用方列出 matches
    std::vector<std::wstring> matches;
};

// 用 [begin, end) 这段词去匹配 candidates(按光标左侧已输入的部分过滤), 改写整段词。
// 唯一候选 -> "候选 + 空格"; 多候选 -> 公共前缀; 补不动 -> list = true
// (此时不改写, 只在光标位于词中、词尾另有内容时仍以整词替换为公共前缀)。
inline Completion complete(State& st, size_t begin, size_t end,
                           const std::vector<std::wstring>& candidates)
{
    Completion result;
    if (begin > st.cursor || end > st.text.size() || begin > end) return result;

    const std::wstring typed = st.text.substr(begin, st.cursor - begin);
    result.matches = filter_prefix(candidates, typed);
    if (result.matches.empty()) return result;

    std::wstring replacement;
    if (result.matches.size() == 1)
    {
        replacement = result.matches.front() + L" ";
    }
    else
    {
        replacement = common_prefix(result.matches);
        if (replacement.size() <= typed.size())
        {
            result.list = true;
            return result;
        }
    }

    const std::wstring segment = st.text.substr(begin, end - begin);
    if (replacement == segment) return result;

    st.text.replace(begin, end - begin, replacement);
    st.cursor = begin + replacement.size();
    result.changed = true;
    return result;
}

} // namespace cesium::shell::lineedit
