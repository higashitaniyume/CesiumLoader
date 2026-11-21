using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("系统字体", "1.1.0", "CesiumLoader",
    "全面替换游戏界面字体（含 FairyGUI 全部 _TMP SDF 字体与旧版动态字体）；支持 Toys「自定义 → 字体」即时切换（含用户目录字体）及本地 .ttf/.otf/.ttc 路径直读，可随时换回游戏默认。",
    Permissions = ModPermission.ModifyGameState,
    SdkVersion = "3.8.0",
    Platforms = ModPlatform.Windows)]
