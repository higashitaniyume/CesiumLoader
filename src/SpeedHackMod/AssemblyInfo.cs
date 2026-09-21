using CesiumLoader.SDK;

// 变速: 用热键实时调整游戏时间流速(Alt+= 加速 / Alt+- 减速 / Delete 开关)。
// 敏感能力: 需要 SpeedHack 权限; 联机对局变速有断线/封号风险(见 docs/mod-SpeedHackMod.md)。
[assembly: ModManifest("变速", "2.1.3", "CesiumLoader",
    "Delete 开关变速; Alt+= / Alt+- 实时调整倍率(默认每次 0.5, 按住可连续调); 倍率/热键都可在 config.json 改",
    Permissions = ModPermission.SpeedHack, SdkVersion = "2.1.3")]
