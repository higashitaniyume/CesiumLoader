// schema.cpp - 加载器配置字段元数据(见 schema.h)

#include "shell/schema.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

namespace
{

const char* const kTaskCtorModes[] = { "auto", "direct", "invoke", nullptr };

using cesium::shell::Apply;
using cesium::shell::Field;
using cesium::shell::ValueType;

// 顺序即 ls 的展示顺序, 按"用户最常改的在前"排:
// 变速 -> 控制台 -> 转发 -> 通道 -> 引导/超时 -> Steam -> 其它。
const Field kFields[] = {
    { "speedhackBaseSpeed", ValueType::Double, 1.0, 100.0, nullptr, Apply::Immediate,
      "启动时的基础倍率; 1 为正常速度, 禁止低于 1" },
    { "consoleTopmost", ValueType::Bool, 0, 0, nullptr, Apply::Immediate,
      "控制台窗口置顶(会一直压着游戏)" },
    { "forwardActivityLog", ValueType::Bool, 0, 0, nullptr, Apply::Immediate,
      "把 mod 行为日志转发到控制台" },
    { "shellEnabled", ValueType::Bool, 0, 0, nullptr, Apply::Immediate,
      "启动调试 shell(关闭后不再读取控制台输入)" },

    { "speedControlEnabled", ValueType::Bool, 0, 0, nullptr, Apply::StartOnly,
      "变速文件通道(<loader>\\speed); 只能从关到开" },
    { "windowControlEnabled", ValueType::Bool, 0, 0, nullptr, Apply::StartOnly,
      "窗口置前文件通道(<loader>\\window); 只能从关到开" },

    { "enabled", ValueType::Bool, 0, 0, nullptr, Apply::Restart,
      "加载器总开关; false 时完全不加载" },
    { "useManagedBootstrap", ValueType::Bool, 0, 0, nullptr, Apply::Restart,
      "实验特性: 由托管 Bootstrap 编排加载" },
    { "consoleEnabled", ValueType::Bool, 0, 0, nullptr, Apply::Restart,
      "启动时分配控制台窗口" },

    { "gameAssemblyTimeoutSec", ValueType::Int, 1, 3600, nullptr, Apply::Restart,
      "等待 GameAssembly.dll 的秒数" },
    { "domainTimeoutSec", ValueType::Int, 1, 3600, nullptr, Apply::Restart,
      "等待 il2cpp domain 的秒数" },
    { "hybridclrTimeoutSec", ValueType::Int, 1, 3600, nullptr, Apply::Restart,
      "等待 HybridCLR 热更程序集的秒数" },

    { "steamBypassEnabled", ValueType::Bool, 0, 0, nullptr, Apply::Restart,
      "Steam 自杀门绕过(非 Steam 客户端启动不退出)" },
    { "steamBypassRestartCheck", ValueType::Bool, 0, 0, nullptr, Apply::Restart,
      "让 SteamAPI_RestartAppIfNecessary 恒返回 0" },
    { "steamBypassMatchmaking", ValueType::Bool, 0, 0, nullptr, Apply::Restart,
      "Steam 大厅匹配绕过(创建/加入房间返回空 Task)" },
    { "steamBypassLobbyHasValue", ValueType::Bool, 0, 0, nullptr, Apply::Restart,
      "Nullable<Lobby>.HasValue 恒 false(阶段3)" },
    { "steamBypassTaskCtorMode", ValueType::Enum, 0, 0, kTaskCtorModes, Apply::Restart,
      "Task<Lobby?> 构造方式 auto/direct/invoke" },
    { "steamBypassLobbyMethods", ValueType::Bool, 0, 0, nullptr, Apply::Restart,
      "备用安全网(实测会导致启动崩溃, 默认关闭)" },
    { "steamBypassLobbyQuery", ValueType::Bool, 0, 0, nullptr, Apply::Restart,
      "LobbyQuery.RequestAsync 恒返回空数组" },

    { "sdkVersion", ValueType::String, 0, 0, nullptr, Apply::Restart,
      "用于校验 mod 声明的 SDK 版本" },
    { "bootstrapAssembly", ValueType::String, 0, 0, nullptr, Apply::Restart,
      "托管引导程序文件名" },
    { "bootstrapType", ValueType::String, 0, 0, nullptr, Apply::Restart,
      "托管引导程序入口类型" },
    { "bootstrapMethod", ValueType::String, 0, 0, nullptr, Apply::Restart,
      "托管引导程序入口方法" },
};

constexpr size_t kFieldCount = sizeof(kFields) / sizeof(kFields[0]);

std::string lower(std::string s)
{
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return (char)std::tolower(c); });
    return s;
}

bool parse_bool(const std::string& text, bool& out)
{
    const std::string v = lower(text);
    if (v == "true" || v == "1" || v == "on" || v == "yes" || v == "y") { out = true; return true; }
    if (v == "false" || v == "0" || v == "off" || v == "no" || v == "n") { out = false; return true; }
    return false;
}

// 严格十进制: 不接受 0x / 指数 / 尾随垃圾(与 speedctl 的解析口径一致)。
bool parse_number(const std::string& text, bool integer_only, double& out, std::string& err)
{
    size_t i = 0;
    const size_t n = text.size();
    while (i < n && std::isspace((unsigned char)text[i])) ++i;
    const size_t start = i;

    if (i < n && (text[i] == '+' || text[i] == '-')) ++i;

    int digits = 0, dots = 0;
    while (i < n && (std::isdigit((unsigned char)text[i]) || text[i] == '.'))
    {
        if (text[i] == '.') ++dots; else ++digits;
        ++i;
    }
    if (digits == 0 || dots > (integer_only ? 0 : 1))
    {
        err = integer_only ? "需要一个整数" : "需要一个数字";
        return false;
    }
    while (i < n && std::isspace((unsigned char)text[i])) ++i;
    if (i != n) { err = "数字后面有多余字符"; return false; }

    const double v = std::strtod(text.c_str() + start, nullptr);
    if (!std::isfinite(v)) { err = "不是有限数字"; return false; }
    out = v;
    return true;
}

std::string format_double(double v)
{
    // %g 去掉无意义尾零: 2.0 -> "2", 0.15 -> "0.15"; 最多 10 位有效数字足够配置用。
    char buf[64];
    std::snprintf(buf, sizeof(buf), "%.10g", v);
    return buf;
}

std::string quote(const std::string& s)
{
    std::string out = "\"";
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
    out += '"';
    return out;
}

// 归一化: 把用户输入变成该字段的规范文本; 失败给 err。
bool normalize(const Field& f, const std::string& text, std::string& value, std::string& err)
{
    switch (f.type)
    {
    case ValueType::Bool:
    {
        bool b = false;
        if (!parse_bool(text, b)) { err = "需要 true/false(也接受 1/0、on/off)"; return false; }
        value = b ? "true" : "false";
        return true;
    }
    case ValueType::Int:
    {
        double v = 0;
        if (!parse_number(text, true, v, err)) return false;
        if (v < f.min_value || v > f.max_value)
        {
            err = "超出范围 [" + format_double(f.min_value) + ", " + format_double(f.max_value) + "]";
            return false;
        }
        value = std::to_string(static_cast<long long>(v));
        return true;
    }
    case ValueType::Double:
    {
        double v = 0;
        if (!parse_number(text, false, v, err)) return false;
        if (v < f.min_value || v > f.max_value)
        {
            err = "超出范围 [" + format_double(f.min_value) + ", " + format_double(f.max_value) + "]";
            return false;
        }
        value = format_double(v);
        return true;
    }
    case ValueType::Enum:
    {
        for (const char* const* p = f.enum_values; p && *p; ++p)
        {
            if (lower(text) == lower(*p)) { value = *p; return true; }
        }
        err = "取值必须是:";
        for (const char* const* p = f.enum_values; p && *p; ++p) err += std::string(" ") + *p;
        return false;
    }
    case ValueType::String:
        value = text;
        return true;
    }
    err = "未知类型";
    return false;
}

} // namespace

namespace cesium::shell
{

const Field* loader_fields(size_t& count)
{
    count = kFieldCount;
    return kFields;
}

const Field* find_loader_field(const std::string& name)
{
    for (size_t i = 0; i < kFieldCount; ++i)
        if (name == kFields[i].name) return &kFields[i];
    return nullptr;
}

std::string field_value(const LoaderConfig& cfg, const Field& f)
{
    const std::string n = f.name;
    if (n == "enabled") return cfg.enabled ? "true" : "false";
    if (n == "useManagedBootstrap") return cfg.useManagedBootstrap ? "true" : "false";
    if (n == "consoleEnabled") return cfg.consoleEnabled ? "true" : "false";
    if (n == "consoleTopmost") return cfg.consoleTopmost ? "true" : "false";
    if (n == "forwardActivityLog") return cfg.forwardActivityLog ? "true" : "false";
    if (n == "shellEnabled") return cfg.shellEnabled ? "true" : "false";
    if (n == "speedControlEnabled") return cfg.speedControlEnabled ? "true" : "false";
    if (n == "windowControlEnabled") return cfg.windowControlEnabled ? "true" : "false";
    if (n == "steamBypassEnabled") return cfg.steamBypassEnabled ? "true" : "false";
    if (n == "steamBypassRestartCheck") return cfg.steamBypassRestartCheck ? "true" : "false";
    if (n == "steamBypassMatchmaking") return cfg.steamBypassMatchmaking ? "true" : "false";
    if (n == "steamBypassLobbyHasValue") return cfg.steamBypassLobbyHasValue ? "true" : "false";
    if (n == "steamBypassLobbyMethods") return cfg.steamBypassLobbyMethods ? "true" : "false";
    if (n == "steamBypassLobbyQuery") return cfg.steamBypassLobbyQuery ? "true" : "false";

    if (n == "speedhackBaseSpeed") return format_double(cfg.speedhackBaseSpeed);
    if (n == "gameAssemblyTimeoutSec") return std::to_string(cfg.gameAssemblyTimeoutSec);
    if (n == "domainTimeoutSec") return std::to_string(cfg.domainTimeoutSec);
    if (n == "hybridclrTimeoutSec") return std::to_string(cfg.hybridclrTimeoutSec);

    if (n == "steamBypassTaskCtorMode") return cfg.steamBypassTaskCtorMode;
    if (n == "sdkVersion") return cfg.sdkVersion;
    if (n == "bootstrapAssembly") return cfg.bootstrapAssembly;
    if (n == "bootstrapType") return cfg.bootstrapType;
    if (n == "bootstrapMethod") return cfg.bootstrapMethod;
    return "";
}

std::string field_type_text(const Field& f)
{
    switch (f.type)
    {
    case ValueType::Bool: return "bool";
    case ValueType::Int:
        return "int " + format_double(f.min_value) + ".." + format_double(f.max_value);
    case ValueType::Double:
        return "number " + format_double(f.min_value) + ".." + format_double(f.max_value);
    case ValueType::String: return "string";
    case ValueType::Enum:
    {
        std::string s = "enum";
        for (const char* const* p = f.enum_values; p && *p; ++p) s += std::string(" ") + *p;
        return s;
    }
    }
    return "?";
}

std::string field_apply_text(const Field& f)
{
    switch (f.apply)
    {
    case Apply::Immediate: return "立即生效";
    case Apply::StartOnly: return "只能开启";
    case Apply::Restart: return "重启后生效";
    }
    return "";
}

bool field_assign(LoaderConfig& cfg, const Field& f, const std::string& text, std::string& err)
{
    std::string v;
    if (!normalize(f, text, v, err)) return false;

    const std::string n = f.name;
    if (n == "enabled") cfg.enabled = (v == "true");
    else if (n == "useManagedBootstrap") cfg.useManagedBootstrap = (v == "true");
    else if (n == "consoleEnabled") cfg.consoleEnabled = (v == "true");
    else if (n == "consoleTopmost") cfg.consoleTopmost = (v == "true");
    else if (n == "forwardActivityLog") cfg.forwardActivityLog = (v == "true");
    else if (n == "shellEnabled") cfg.shellEnabled = (v == "true");
    else if (n == "speedControlEnabled") cfg.speedControlEnabled = (v == "true");
    else if (n == "windowControlEnabled") cfg.windowControlEnabled = (v == "true");
    else if (n == "steamBypassEnabled") cfg.steamBypassEnabled = (v == "true");
    else if (n == "steamBypassRestartCheck") cfg.steamBypassRestartCheck = (v == "true");
    else if (n == "steamBypassMatchmaking") cfg.steamBypassMatchmaking = (v == "true");
    else if (n == "steamBypassLobbyHasValue") cfg.steamBypassLobbyHasValue = (v == "true");
    else if (n == "steamBypassLobbyMethods") cfg.steamBypassLobbyMethods = (v == "true");
    else if (n == "steamBypassLobbyQuery") cfg.steamBypassLobbyQuery = (v == "true");
    else if (n == "speedhackBaseSpeed") cfg.speedhackBaseSpeed = std::strtod(v.c_str(), nullptr);
    else if (n == "gameAssemblyTimeoutSec") cfg.gameAssemblyTimeoutSec = (unsigned)std::strtoul(v.c_str(), nullptr, 10);
    else if (n == "domainTimeoutSec") cfg.domainTimeoutSec = (unsigned)std::strtoul(v.c_str(), nullptr, 10);
    else if (n == "hybridclrTimeoutSec") cfg.hybridclrTimeoutSec = (unsigned)std::strtoul(v.c_str(), nullptr, 10);
    else if (n == "steamBypassTaskCtorMode") cfg.steamBypassTaskCtorMode = v;
    else if (n == "sdkVersion") cfg.sdkVersion = v;
    else if (n == "bootstrapAssembly") cfg.bootstrapAssembly = v;
    else if (n == "bootstrapType") cfg.bootstrapType = v;
    else if (n == "bootstrapMethod") cfg.bootstrapMethod = v;
    else { err = "未知字段"; return false; }
    return true;
}

std::string field_json_literal(const Field& f, const std::string& text)
{
    std::string v, err;
    if (!normalize(f, text, v, err)) return "";
    switch (f.type)
    {
    case ValueType::Bool:
    case ValueType::Int:
    case ValueType::Double:
        return v;
    case ValueType::String:
    case ValueType::Enum:
        return quote(v);
    }
    return "";
}

} // namespace cesium::shell
