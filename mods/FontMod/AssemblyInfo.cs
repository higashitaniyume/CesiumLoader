using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("系统字体", "1.0.0", "CesiumLoader",
    "把游戏界面字体替换成系统里已安装的字体；在 Toys「自定义 → 字体」里挑一张卡片即可，游戏中即时生效，可随时换回游戏默认。",
    Permissions = ModPermission.ModifyGameState,
    SdkVersion = "3.8.0", Platforms = ModPlatform.Windows)]
