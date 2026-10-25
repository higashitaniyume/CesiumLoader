// jsonc_patch.cpp - 顶层键值的文本级改写(见 jsonc_patch.h)

#include "config/jsonc_patch.h"

#include <cstdint>

namespace
{

const size_t kNpos = static_cast<size_t>(-1);

bool is_space(char c)
{
    return c == ' ' || c == '\t' || c == '\r' || c == '\n';
}

// 跳过空白与 //、/* */ 注释。配置文件里的注释必须被跳过, 否则键定位会错。
size_t skip_trivia(const std::string& s, size_t i)
{
    for (;;)
    {
        while (i < s.size() && is_space(s[i])) ++i;
        if (i + 1 < s.size() && s[i] == '/' && s[i + 1] == '/')
        {
            while (i < s.size() && s[i] != '\n') ++i;
            continue;
        }
        if (i + 1 < s.size() && s[i] == '/' && s[i + 1] == '*')
        {
            i += 2;
            while (i + 1 < s.size() && !(s[i] == '*' && s[i + 1] == '/')) ++i;
            i = (i + 1 < s.size()) ? i + 2 : s.size();
            continue;
        }
        return i;
    }
}

// s[i] == '"', 返回值后一位(跳过转义)。
size_t skip_string(const std::string& s, size_t i)
{
    ++i;
    while (i < s.size())
    {
        if (s[i] == '\\') { i += 2; continue; }
        if (s[i] == '"') return i + 1;
        ++i;
    }
    return s.size();
}

// 从值的首字符开始, 返回值结束位置(不含尾随空白)。
size_t skip_value(const std::string& s, size_t i)
{
    i = skip_trivia(s, i);
    if (i >= s.size()) return i;

    const char c = s[i];
    if (c == '"') return skip_string(s, i);

    if (c == '{' || c == '[')
    {
        const char open = c;
        const char close = (c == '{') ? '}' : ']';
        int depth = 0;
        while (i < s.size())
        {
            const char ch = s[i];
            if (ch == '"') { i = skip_string(s, i); continue; }
            if (ch == open) ++depth;
            else if (ch == close)
            {
                --depth;
                ++i;
                if (depth == 0) return i;
                continue;
            }
            ++i;
        }
        return s.size();
    }

    // 标量: 一直到 , } ] 或注释起点
    while (i < s.size())
    {
        const char ch = s[i];
        if (ch == ',' || ch == '}' || ch == ']') break;
        if (ch == '/' && i + 1 < s.size() && (s[i + 1] == '/' || s[i + 1] == '*')) break;
        ++i;
    }
    while (i > 0 && is_space(s[i - 1])) --i;
    return i;
}

std::string unescape_key(const std::string& s)
{
    std::string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i)
    {
        if (s[i] != '\\' || i + 1 >= s.size()) { out += s[i]; continue; }
        const char n = s[++i];
        switch (n)
        {
        case '"': out += '"'; break;
        case '\\': out += '\\'; break;
        case '/': out += '/'; break;
        case 'n': out += '\n'; break;
        case 't': out += '\t'; break;
        case 'r': out += '\r'; break;
        default: out += n; break;
        }
    }
    return out;
}

std::string escape(const std::string& s)
{
    std::string out;
    out.reserve(s.size() + 8);
    for (char c : s)
    {
        switch (c)
        {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default: out += c; break;
        }
    }
    return out;
}

// 根对象的 '{' 位置(跳过 BOM 与空白/注释); 不是对象返回 kNpos。
size_t root_open(const std::string& s)
{
    size_t i = 0;
    if (s.size() >= 3 && static_cast<unsigned char>(s[0]) == 0xEF &&
        static_cast<unsigned char>(s[1]) == 0xBB && static_cast<unsigned char>(s[2]) == 0xBF)
        i = 3;
    i = skip_trivia(s, i);
    if (i >= s.size() || s[i] != '{') return kNpos;
    return i;
}

// 与 open 处的 '{' 配对的 '}' 位置。
size_t matching_close(const std::string& s, size_t open)
{
    int depth = 0;
    for (size_t i = open; i < s.size();)
    {
        const char c = s[i];
        if (c == '"') { i = skip_string(s, i); continue; }
        if (c == '{') { ++depth; ++i; continue; }
        if (c == '}')
        {
            --depth;
            ++i;
            if (depth == 0) return i - 1;
            continue;
        }
        if (c == '/' && i + 1 < s.size() && s[i + 1] == '/')
        {
            while (i < s.size() && s[i] != '\n') ++i;
            continue;
        }
        if (c == '/' && i + 1 < s.size() && s[i + 1] == '*')
        {
            i += 2;
            while (i + 1 < s.size() && !(s[i] == '*' && s[i + 1] == '/')) ++i;
            i = (i + 1 < s.size()) ? i + 2 : s.size();
            continue;
        }
        ++i;
    }
    return kNpos;
}

} // namespace

namespace cesium::jsonc
{

bool find_top_level_value(const std::string& text, const std::string& key, size_t& begin, size_t& end)
{
    const size_t open = root_open(text);
    if (open == kNpos) return false;

    size_t i = open + 1;
    for (;;)
    {
        i = skip_trivia(text, i);
        if (i >= text.size()) return false;
        if (text[i] == '}') return false;
        if (text[i] != '"') return false;

        const size_t key_end = skip_string(text, i);
        const std::string k = unescape_key(text.substr(i + 1, key_end - i - 2));
        i = skip_trivia(text, key_end);
        if (i >= text.size() || text[i] != ':') return false;
        ++i;

        const size_t value_begin = skip_trivia(text, i);
        const size_t value_end = skip_value(text, value_begin);
        if (k == key)
        {
            begin = value_begin;
            end = value_end;
            return true;
        }

        i = skip_trivia(text, value_end);
        if (i < text.size() && text[i] == ',') { ++i; continue; }
        return false;
    }
}

bool set_top_level_value(const std::string& text, const std::string& key,
                         const std::string& literal, std::string& out)
{
    size_t begin = 0, end = 0;
    if (find_top_level_value(text, key, begin, end))
    {
        out = text.substr(0, begin) + literal + text.substr(end);
        return true;
    }

    const size_t open = root_open(text);
    if (open == kNpos) return false;
    const size_t close = matching_close(text, open);
    if (close == kNpos) return false;

    const std::string pair = "\"" + escape(key) + "\": " + literal;

    // 根对象为空: 插在 '{' 之后, 补两行缩进。
    size_t first = skip_trivia(text, open + 1);
    if (first >= close)
    {
        out = text.substr(0, open + 1) + "\n  " + pair + "\n" + text.substr(open + 1);
        return true;
    }

    // 非空: 插在最后一个非空白字符之后, 前面补逗号。
    size_t last = close;
    while (last > open + 1 && is_space(text[last - 1])) --last;
    out = text.substr(0, last) + ",\n  " + pair + text.substr(last);
    return true;
}

void list_top_level_keys(const std::string& text, std::vector<std::string>& keys)
{
    keys.clear();
    const size_t open = root_open(text);
    if (open == kNpos) return;

    size_t i = open + 1;
    for (;;)
    {
        i = skip_trivia(text, i);
        if (i >= text.size() || text[i] == '}') return;
        if (text[i] != '"') return;

        const size_t key_end = skip_string(text, i);
        keys.push_back(unescape_key(text.substr(i + 1, key_end - i - 2)));
        i = skip_trivia(text, key_end);
        if (i >= text.size() || text[i] != ':') return;
        ++i;

        const size_t value_begin = skip_trivia(text, i);
        const size_t value_end = skip_value(text, value_begin);
        i = skip_trivia(text, value_end);
        if (i < text.size() && text[i] == ',') { ++i; continue; }
        return;
    }
}

} // namespace cesium::jsonc
