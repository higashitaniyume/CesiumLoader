// request.h - window\request.txt 的纯解析(不依赖 Windows API, 可直接单测)
//
// 协议(对旧 mod 完全兼容):
//   第 1 行: 十进制令牌。加载器只处理"内容变化"的请求; 启动时写入的 "0" 是重置值。
//   第 2 行: 可选, 提示音的**绝对路径**(UTF-8)。缺省/空 = 只置前, 不播放。
//   第 3 行起: 忽略(给将来扩展留位置)。
// 行尾: CRLF / LF / CR 均可; 每行首尾的空格与制表符忽略。
//
// 为什么路径跟令牌放在同一个文件: "置前 + 提示音"是一个动作。放在一次请求里,
// 加载器才能在窗口已在前台时把两者一起静默跳过, 不会出现"窗口没动却响了"。
//
// 安全边界: 路径必须是绝对路径且不含控制字符。相对路径会被丢弃(否则会按游戏
// 工作目录解析, 行为不确定); 真正的文件存在性/扩展名/大小由 windowctl 再校验。

#pragma once

#include <string>

namespace cesium::windowing
{

struct FocusRequest
{
    std::string token;        // 十进制令牌, 已 trim
    std::string sound_path;   // UTF-8 绝对路径, 空表示无提示音

    bool has_sound() const { return !sound_path.empty(); }
    bool valid() const { return !token.empty() && token != "0"; }
};

// 解析请求文本。非法令牌(空/0/非数字/过长)得到 valid()==false。
// 提示音路径不合法(相对路径/含控制字符/过长)时只清空 sound_path, 不影响 valid()。
inline FocusRequest parse_focus_request(const std::string& text)
{
    FocusRequest req;
    std::string line;
    int index = 0;
    bool stop = false;

    const size_t kMaxTokenChars = 64;
    const size_t kMaxPathChars = 4096;

    auto trim = [](const std::string& s)
    {
        size_t b = 0, e = s.size();
        while (b < e && (s[b] == ' ' || s[b] == '\t')) ++b;
        while (e > b && (s[e - 1] == ' ' || s[e - 1] == '\t')) --e;
        return s.substr(b, e - b);
    };

    auto all_digits = [](const std::string& s)
    {
        if (s.empty()) return false;
        for (size_t i = 0; i < s.size(); i++)
            if (s[i] < '0' || s[i] > '9') return false;
        return true;
    };

    auto has_control = [](const std::string& s)
    {
        for (size_t i = 0; i < s.size(); i++)
            if (static_cast<unsigned char>(s[i]) < 0x20) return true;
        return false;
    };

    auto is_absolute = [](const std::string& s)
    {
        // 必须是 "C:\..." / "C:/..." 或 UNC "\\server\...";
        // 单独的 "C:ding.wav" 是驱动器相对路径, 会按当前目录解析, 一律不接受。
        if (s.size() >= 3 && s[1] == ':' && (s[2] == '\\' || s[2] == '/')) return true;
        if (s.size() >= 2 && s[0] == '\\' && s[1] == '\\') return true;
        return false;
    };

    auto take_line = [&]()
    {
        std::string t = trim(line);
        line.clear();
        if (t.empty()) return;

        if (index == 0)
        {
            if (t.size() <= kMaxTokenChars && all_digits(t)) req.token = t;
            // 令牌非法(或就是启动时的重置值 0): 整条请求作废, 后面几行没有意义。
            if (req.token.empty() || req.token == "0") stop = true;
        }
        else if (index == 1)
        {
            if (t.size() <= kMaxPathChars && !has_control(t) && is_absolute(t))
                req.sound_path = t;
        }

        ++index;
    };

    for (size_t i = 0; i < text.size() && !stop; i++)
    {
        char c = text[i];
        if (c == '\r' || c == '\n')
        {
            take_line();
            if (stop) break;
            continue;
        }
        line.push_back(c);
    }
    if (!stop) take_line();   // 最后一行没有换行符

    return req;
}

} // namespace cesium::windowing
