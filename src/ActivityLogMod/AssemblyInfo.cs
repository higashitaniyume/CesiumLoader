using CesiumLoader.SDK;

// mod 元数据: 程序集级声明(权威位置, 读取时不触发类型加载, 兼容 HybridCLR)。
// 敏感权限(GameActions/SpeedHack)默认关闭, 需要时在此声明, 并可用
// mods\{程序集名}.permissions.json 逐项覆盖。
[assembly: ModManifest("实时行为日志", "2.1.2", "CesiumLoader",
    "把对局内的行为(用牌/骰子/移动/战斗等)实时输出到加载器控制台",
    Permissions = ModPermission.ReadGameState, SdkVersion = "2.1.2")]
