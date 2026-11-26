using CesiumLoader.SDK.Manifests;

[assembly: ModManifest(
    "组队大厅联动",
    "1.0.0",
    "CesiumLoader",
    "连接 AstralParty.Toys 组队大厅：房主开房自动捕获房间码与密码、队员一键进房、对局结束自动回传即时战报与回放 ID",
    Permissions = ModPermission.ReadGameState
        | ModPermission.GameActions
        | ModPermission.FileWrite
        | ModPermission.FileSystem
        | ModPermission.Network,
    SdkVersion = "3.8.0",
    Platforms = ModPlatform.Windows | ModPlatform.Android)]
