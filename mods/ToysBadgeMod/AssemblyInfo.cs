using CesiumLoader.SDK.Manifests;

[assembly: ModManifest(
    "工具箱玩家标识",
    "1.0.1",
    "Aperfish",
    "对局内自动在同样使用 Astral Party Toys（吉星派对工具箱）的玩家头像左侧显示圆形工具箱图标。",
    Permissions = ModPermission.ReadGameState | ModPermission.UI | ModPermission.Network,
    SdkVersion = "3.8.0",
    Platforms = ModPlatform.Windows | ModPlatform.Android)]
