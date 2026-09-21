// config.cpp - Doorstop 式配置解析 (doorstop_config.json)
//
// 极简 JSON 扫描器: 只取顶层 key:value, 支持 bool / 整数 / 字符串。
// 不需要完整 JSON 解析 —— 配置是我们自己生成的, 格式受控。
// 任何解析失败都回退默认值, 绝不中断进程。

#include "config.h"

#include "loader.h"
#include "speedhack.h"   // kSpeedMin / kSpeedMax: 倍率区间与引擎共用一份定义

#include <fstream>
#include <sstream>
#include <cstdlib>
#include <cstring>

namespace
{

std::string read_file(const std::wstring& path)
{
    std::ifstream in(path, std::ios::binary);
    if (!in) return "";
    return std::string((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

// 在 json 中定位 "key" 后的 ':' 值起始位置; 找不到返回 npos
size_t value_start(const std::string& json, const char* key)
{
    std::string needle = std::string("\"") + key + "\"";
    size_t pos = json.find(needle);
    if (pos == std::string::npos) return std::string::npos;
    pos = json.find(':', pos);
    if (pos == std::string::npos) return std::string::npos;
    pos++;
    while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\r' || json[pos] == '\n'))
        pos++;
    return pos;
}

bool json_bool(const std::string& json, const char* key, bool def)
{
    size_t pos = value_start(json, key);
    if (pos == std::string::npos) return def;
    if (json.compare(pos, 4, "true") == 0) return true;
    if (json.compare(pos, 5, "false") == 0) return false;
    return def;
}

unsigned json_uint(const std::string& json, const char* key, unsigned def)
{
    size_t pos = value_start(json, key);
    if (pos == std::string::npos) return def;
    long v = strtol(json.c_str() + pos, nullptr, 10);
    if (v < 1 || v > 3600) return def;   // 防御: 超时范围 [1, 3600]
    return static_cast<unsigned>(v);
}

double json_double(const std::string& json, const char* key, double def)
{
    size_t pos = value_start(json, key);
    if (pos == std::string::npos) return def;
    double v = strtod(json.c_str() + pos, nullptr);
    // 防御: 倍率范围 [1, 100] —— 低于 1 倍(减速)被硬性禁止, 非法值一律退回默认(1.0)。
    if (!(v >= kSpeedMin && v <= kSpeedMax)) return def;
    return v;
}

std::string json_string(const std::string& json, const char* key, const std::string& def)
{
    size_t pos = value_start(json, key);
    if (pos == std::string::npos || pos >= json.size() || json[pos] != '"') return def;
    pos++;
    std::string out;
    while (pos < json.size() && json[pos] != '"')
    {
        if (json[pos] == '\\' && pos + 1 < json.size())
        {
            char c = json[pos + 1];
            switch (c)
            {
                case 'n': out.push_back('\n'); break;
                case 't': out.push_back('\t'); break;
                case 'r': out.push_back('\r'); break;
                default:  out.push_back(c); break;
            }
            pos += 2;
        }
        else
        {
            out.push_back(json[pos]);
            pos++;
        }
    }
    return out;
}

} // namespace

LoaderConfig load_config(const std::wstring& config_path)
{
    LoaderConfig cfg;
    std::string json = read_file(config_path);
    if (json.empty()) return cfg;   // 无配置文件 = 全默认(加载器启用)

    cfg.enabled = json_bool(json, "enabled", true);
    cfg.useManagedBootstrap = json_bool(json, "useManagedBootstrap", false);
    cfg.bootstrapAssembly = json_string(json, "bootstrapAssembly", cfg.bootstrapAssembly);
    cfg.bootstrapType = json_string(json, "bootstrapType", cfg.bootstrapType);
    cfg.bootstrapMethod = json_string(json, "bootstrapMethod", cfg.bootstrapMethod);
    cfg.gameAssemblyTimeoutSec = json_uint(json, "gameAssemblyTimeoutSec", cfg.gameAssemblyTimeoutSec);
    cfg.domainTimeoutSec = json_uint(json, "domainTimeoutSec", cfg.domainTimeoutSec);
    cfg.hybridclrTimeoutSec = json_uint(json, "hybridclrTimeoutSec", cfg.hybridclrTimeoutSec);
    cfg.consoleEnabled = json_bool(json, "consoleEnabled", true);
    cfg.consoleTopmost = json_bool(json, "consoleTopmost", true);
    cfg.forwardActivityLog = json_bool(json, "forwardActivityLog", true);
    cfg.speedhackBaseSpeed = json_double(json, "speedhackBaseSpeed", 1.0);
    cfg.speedControlEnabled = json_bool(json, "speedControlEnabled", true);
    cfg.sdkVersion = json_string(json, "sdkVersion", cfg.sdkVersion);

    // 记录加载到的配置, 便于排查
    log_line("[config] enabled=" + std::string(cfg.enabled ? "true" : "false"));
    log_line("[config] bootstrap=" + cfg.bootstrapAssembly);
    return cfg;
}
