using CesiumLoader.SDK;

// 变速: 用热键实时调整游戏时间流速(Alt+= 加速 / Alt+- 减速 / Delete 在 1.0x 与刚才的倍率间切换)。
// 敏感能力: 需要 SpeedHack 权限; 倍率下限硬性 1.0(不能减速), 别调太高(见 docs/mod-SpeedHackMod.md)。
[assembly: ModManifest("变速", "2.2.0", "CesiumLoader",
    "Delete 在 1.0x 与刚才的倍率之间切换(再按一次切回); Alt+= / Alt+- 实时调整倍率(1.0x 起, 默认每次 0.5, 按住可连续调); 倍率/热键都可在 config.json 改",
    Permissions = ModPermission.SpeedHack, SdkVersion = "2.2.0")]
