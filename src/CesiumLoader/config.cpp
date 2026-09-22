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
    // Steam 自杀门绕过: 缺失/损坏走默认(启用), 绝不阻止启动(见 steamhack.h)。
    cfg.steamBypassEnabled = json_bool(json, "steamBypassEnabled", true);
    cfg.steamBypassRestartCheck = json_bool(json, "steamBypassRestartCheck", true);
    // 阶段2: 大厅匹配绕过(CreateLobbyAsync/JoinLobbyAsync -> 已完成的空 Task)。
    // 同样缺失/损坏走默认(启用), 绝不阻止启动。
    cfg.steamBypassMatchmaking = json_bool(json, "steamBypassMatchmaking", true);
    // 阶段3: Nullable<Lobby>.get_HasValue 恒返回 false(修 val.HasValue 误判 -> SetPublic NRE)。
    // 同样缺失/损坏走默认(启用), 绝不阻止启动。
    cfg.steamBypassLobbyHasValue = json_bool(json, "steamBypassLobbyHasValue", true);
    // 方案C: Task<T>..ctor(T) 的调用方式 "auto"(默认) / "direct" / "invoke"。
    // 字符串原样读入, 合法性校验与降级日志在 steamhack_install 里做(非法值按 "auto")。
    cfg.steamBypassTaskCtorMode = json_string(json, "steamBypassTaskCtorMode", cfg.steamBypassTaskCtorMode);
    // 方案B(备用安全网): Lobby.SetPublic/SetJoinable/Id 挂成无害。
    // **缺失/损坏走默认 false, 不要改回 true**: 实机证明 true 会让游戏启动早期崩溃
    // (0xC0000005 / GameAssembly.dll, 崩溃前最后一条日志是方案B 的 "Lobby.get_Id 首次被拦截");
    // 改回 false 后同样配置完全正常。现在也不需要它 —— 方案C 已把 Task 结果构造成真正的空
    // Nullable(HasValue=false), SetPublic/SetJoinable 不会被调用; 它只是"hasValue 若又变 true"
    // 时的备用安全网, 启用前需自行实机验证。详见 config.h 同名字段的注释。
    cfg.steamBypassLobbyMethods = json_bool(json, "steamBypassLobbyMethods", false);
    // 阶段5: LobbyQuery.RequestAsync 恒返回"结果为空 Lobby[] 的已完成 Task"。
    // 同样缺失/损坏走默认(启用), 绝不阻止启动。独立开关, 与上面几个互不影响。
    cfg.steamBypassLobbyQuery = json_bool(json, "steamBypassLobbyQuery", true);
    cfg.sdkVersion = json_string(json, "sdkVersion", cfg.sdkVersion);

    // 记录加载到的配置, 便于排查
    log_line("[config] enabled=" + std::string(cfg.enabled ? "true" : "false"));
    log_line("[config] bootstrap=" + cfg.bootstrapAssembly);
    return cfg;
}
