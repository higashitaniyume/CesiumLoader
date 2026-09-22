// config.cpp - Doorstop 式配置解析 (doorstop_config.json)
//
// 解析用 nlohmann/json(统一封装见 jsonc.h), 不再手写 JSON 扫描器:
//   - 接受 JSONC 注释 —— 本配置实测就是带 // 注释的(见 jsonc.h 的说明);
//   - 剥离 UTF-8 BOM;
//   - 解析失败/键缺失/类型不符一律退回默认值, 绝不中断进程。
//
// 各字段的默认值取自 LoaderConfig 的成员初始值(config.h), 不再在调用处重复字面量。

#include "config.h"

#include "jsonc.h"       // JSONC 读取(nlohmann/json 封装: 允许注释、剥离 BOM、失败不抛异常)
#include "loader.h"
#include "speedhack.h"   // kSpeedMin / kSpeedMax: 倍率区间与引擎共用一份定义

#include <fstream>

// 本文件的 LoaderConfig / load_config 在全局作用域(config.h 未开命名空间),
// 而取值助手都在 cesium::jsonc 下, 这里给个短别名。
namespace jsonc = cesium::jsonc;

namespace
{

std::string read_file(const std::wstring& path)
{
    std::ifstream in(path, std::ios::binary);
    if (!in) return "";
    return std::string((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

// 超时(秒): 超出 [1, 3600] 视为非法, 退回默认值(与旧实现一致)。
unsigned read_timeout_sec(const jsonc::Json& j, const char* key, unsigned def)
{
    const int64_t v = jsonc::integer(j, key, def);
    if (v < 1 || v > 3600) return def;   // 防御: 超时范围 [1, 3600]
    return static_cast<unsigned>(v);
}

// 倍率: 超出 [kSpeedMin, kSpeedMax] 视为非法, 退回默认值(与旧实现一致)。
// 低于 1 倍(减速)被硬性禁止 —— 见 speedhack.h 的 kSpeedMin。
double read_speed(const jsonc::Json& j, const char* key, double def)
{
    const double v = jsonc::number(j, key, def);
    if (!(v >= kSpeedMin && v <= kSpeedMax)) return def;
    return v;
}

} // namespace

LoaderConfig load_config(const std::wstring& config_path)
{
    LoaderConfig cfg;
    const std::string text = read_file(config_path);
    if (text.empty()) return cfg;   // 无配置文件 = 全默认(加载器启用)

    const jsonc::Json j = jsonc::parse(text);
    if (j.is_discarded())
    {
        // 文件存在但解析失败 → 整体退回默认值, 并且**必须大声记一条**:
        // 否则用户改坏了配置(少个逗号/引号不配对)却只看到"设置没生效", 完全无从下手。
        // 注意与旧实现的差别: 旧的手写扫描器是"能扫到哪个键就用哪个键", 语法错误只影响局部;
        // 新实现一旦语法不合法就整体用默认值。等价性测试里对此有专门的用例(见 tests\native)。
        log_line("[config] 警告: doorstop_config.json 解析失败, 已使用**全部默认值**; "
                 "请检查 JSON 语法(// 注释是允许的; 多余逗号、单引号、引号不配对都会失败)");
        return cfg;
    }

    cfg.enabled = jsonc::boolean(j, "enabled", cfg.enabled);
    cfg.useManagedBootstrap = jsonc::boolean(j, "useManagedBootstrap", cfg.useManagedBootstrap);
    cfg.bootstrapAssembly = jsonc::str(j, "bootstrapAssembly", cfg.bootstrapAssembly);
    cfg.bootstrapType = jsonc::str(j, "bootstrapType", cfg.bootstrapType);
    cfg.bootstrapMethod = jsonc::str(j, "bootstrapMethod", cfg.bootstrapMethod);
    cfg.gameAssemblyTimeoutSec = read_timeout_sec(j, "gameAssemblyTimeoutSec", cfg.gameAssemblyTimeoutSec);
    cfg.domainTimeoutSec = read_timeout_sec(j, "domainTimeoutSec", cfg.domainTimeoutSec);
    cfg.hybridclrTimeoutSec = read_timeout_sec(j, "hybridclrTimeoutSec", cfg.hybridclrTimeoutSec);
    cfg.consoleEnabled = jsonc::boolean(j, "consoleEnabled", cfg.consoleEnabled);
    // 注意: 默认值取自 config.h 的 consoleTopmost(= false, "置顶会一直压着游戏")。
    // 旧实现这里硬编码传了 true, 与头文件默认值相反, 导致配置里缺该键时控制台会置顶 ——
    // 已按头文件承诺修正。实测仓内与实机 4 份配置都显式写了 consoleTopmost=false,
    // 因此该修正对现有配置**没有任何行为变化**。
    cfg.consoleTopmost = jsonc::boolean(j, "consoleTopmost", cfg.consoleTopmost);
    cfg.forwardActivityLog = jsonc::boolean(j, "forwardActivityLog", cfg.forwardActivityLog);
    cfg.speedhackBaseSpeed = read_speed(j, "speedhackBaseSpeed", cfg.speedhackBaseSpeed);
    cfg.speedControlEnabled = jsonc::boolean(j, "speedControlEnabled", cfg.speedControlEnabled);
    // Steam 自杀门绕过: 缺失/损坏走默认(启用), 绝不阻止启动(见 steamhack.h)。
    cfg.steamBypassEnabled = jsonc::boolean(j, "steamBypassEnabled", cfg.steamBypassEnabled);
    cfg.steamBypassRestartCheck = jsonc::boolean(j, "steamBypassRestartCheck", cfg.steamBypassRestartCheck);
    // 阶段2: 大厅匹配绕过(CreateLobbyAsync/JoinLobbyAsync -> 已完成的空 Task)。
    // 同样缺失/损坏走默认(启用), 绝不阻止启动。
    cfg.steamBypassMatchmaking = jsonc::boolean(j, "steamBypassMatchmaking", cfg.steamBypassMatchmaking);
    // 阶段3: Nullable<Lobby>.get_HasValue 恒返回 false(修 val.HasValue 误判 -> SetPublic NRE)。
    // 同样缺失/损坏走默认(启用), 绝不阻止启动。
    cfg.steamBypassLobbyHasValue = jsonc::boolean(j, "steamBypassLobbyHasValue", cfg.steamBypassLobbyHasValue);
    // 方案C: Task<T>..ctor(T) 的调用方式 "auto"(默认) / "direct" / "invoke"。
    // 字符串原样读入, 合法性校验与降级日志在 steamhack_install 里做(非法值按 "auto")。
    cfg.steamBypassTaskCtorMode = jsonc::str(j, "steamBypassTaskCtorMode", cfg.steamBypassTaskCtorMode);
    // 方案B(备用安全网): Lobby.SetPublic/SetJoinable/Id 挂成无害。
    // **缺失/损坏走默认 false, 不要改回 true**: 实机证明 true 会让游戏启动早期崩溃
    // (0xC0000005 / GameAssembly.dll, 崩溃前最后一条日志是方案B 的 "Lobby.get_Id 首次被拦截");
    // 改回 false 后同样配置完全正常。现在也不需要它 —— 方案C 已把 Task 结果构造成真正的空
    // Nullable(HasValue=false), SetPublic/SetJoinable 不会被调用; 它只是"hasValue 若又变 true"
    // 时的备用安全网, 启用前需自行实机验证。详见 config.h 同名字段的注释。
    cfg.steamBypassLobbyMethods = jsonc::boolean(j, "steamBypassLobbyMethods", cfg.steamBypassLobbyMethods);
    // 阶段5: LobbyQuery.RequestAsync 恒返回"结果为空 Lobby[] 的已完成 Task"。
    // 同样缺失/损坏走默认(启用), 绝不阻止启动。独立开关, 与上面几个互不影响。
    cfg.steamBypassLobbyQuery = jsonc::boolean(j, "steamBypassLobbyQuery", cfg.steamBypassLobbyQuery);
    cfg.sdkVersion = jsonc::str(j, "sdkVersion", cfg.sdkVersion);

    // 记录加载到的配置, 便于排查
    log_line("[config] enabled=" + std::string(cfg.enabled ? "true" : "false"));
    log_line("[config] bootstrap=" + cfg.bootstrapAssembly);
    return cfg;
}
