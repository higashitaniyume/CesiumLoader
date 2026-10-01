using CesiumLoader.SDK;

// mod 元数据: 程序集级声明(权威位置, 读取时不触发类型加载, 兼容 HybridCLR)。
//
// 权限说明(为什么这个 mod 要的最多):
//   ReadGameState —— 读局面(玩家/怪物/手牌/buff)才能给 agent 观测;
//   GameActions   —— **敏感**: 代替玩家发 C2S(投骰/移动/用牌/选筹码), 这是"接管"的本质;
//   SpeedHack     —— **敏感**: astral_speed 工具(复用加载器变速通道), 只为加快 agent 的等待;
//   FileWrite     —— 写自己的 config/日志(mods 目录内);
//   FileSystem    —— 桥接目录在 %LocalAppData%\AstralParty_ModLoader\agent(mods 目录**之外**),
//                    状态/事件/命令/结果文件都写在那里, 所以必须如实声明这一位。
// 不申请 ModifyGameState(内存改写)与 Camera/Input/UI: 这个 mod 只做"像玩家一样操作"。
[assembly: ModManifest("AI Agent 桥接", "1.0.0", "CesiumLoader",
    "把对局暴露成文件通道(bridge): 状态快照 + 事件流 + 原始动作流 + 命令执行, 供外部 MCP server 让 AI agent 接管对局",
    Permissions = ModPermission.ReadGameState | ModPermission.GameActions | ModPermission.SpeedHack | ModPermission.FileWrite | ModPermission.FileSystem,
    SdkVersion = "2.2.4")]
