// command.h - shell 命令行分词(纯逻辑, 可单测)
//
// 规则(刻意保持简单, 只解决"值里带空格"这一个真问题):
//   - 空白分隔参数;
//   - 双引号包住一段整体, 引号本身不进入参数;
//   - 反斜杠**不做转义** —— Windows 路径里全是反斜杠, 当转义符只会制造麻烦;
//   - 空行返回空参数表(不是错误); 引号没闭合才算错误。

#pragma once

#include <string>
#include <vector>

namespace cesium::shell
{

struct ParseResult
{
    bool ok = true;
    std::string error;
    std::vector<std::string> args;
};

ParseResult tokenize(const std::string& line);

// 去掉首尾空白。
std::string trim(const std::string& text);

// ASCII 小写(命令名与字段名都是 ASCII, 不用处理 Unicode 大小写)。
std::string lower_ascii(const std::string& text);

// 参数里是否出现 --help / -h / help(用于"命令 --help")。
bool wants_help(const std::vector<std::string>& args);

} // namespace cesium::shell
