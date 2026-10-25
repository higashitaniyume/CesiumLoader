// command.cpp - 命令行分词(见 command.h)

#include "shell/command.h"

#include <cctype>

namespace cesium::shell
{

std::string trim(const std::string& text)
{
    size_t begin = 0, end = text.size();
    while (begin < end && std::isspace((unsigned char)text[begin])) ++begin;
    while (end > begin && std::isspace((unsigned char)text[end - 1])) --end;
    return text.substr(begin, end - begin);
}

std::string lower_ascii(const std::string& text)
{
    std::string out = text;
    for (char& c : out)
        if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
    return out;
}

ParseResult tokenize(const std::string& line)
{
    ParseResult result;
    std::string current;
    bool in_quotes = false;
    bool has_token = false;

    for (char c : line)
    {
        if (c == '"')
        {
            in_quotes = !in_quotes;
            has_token = true;   // "" 也算一个空参数
            continue;
        }

        if (!in_quotes && std::isspace((unsigned char)c))
        {
            if (has_token || !current.empty())
            {
                result.args.push_back(current);
                current.clear();
                has_token = false;
            }
            continue;
        }

        current += c;
        has_token = true;
    }

    if (in_quotes)
    {
        result.ok = false;
        result.error = "引号没有闭合";
        return result;
    }

    if (has_token || !current.empty()) result.args.push_back(current);
    return result;
}

bool wants_help(const std::vector<std::string>& args)
{
    for (const std::string& a : args)
        if (a == "--help" || a == "-h") return true;
    return false;
}

} // namespace cesium::shell
