using CesiumLoader.SDK;

// mod 元数据: 程序集级声明(权威位置, 读取时不触发类型加载, 兼容 HybridCLR)。
// 敏感权限(GameActions/SpeedHack)默认关闭, 需要时在此声明, 并可用
// mods\{程序集名}.permissions.json 逐项覆盖。
[assembly: ModManifest("游戏变速", "1.0.0", "CesiumLoader",
    "热键控制游戏时间流速(SpeedHack SDK 接口示例)",
    Permissions = ModPermission.SpeedHack, SdkVersion = "2.0.0")]
