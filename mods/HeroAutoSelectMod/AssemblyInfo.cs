using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("自动选角色（示例）", "1.0.0", "CesiumLoader",
    "匹配成功或进入选人阶段后自动选择指定角色（默认 102 芬妮「古怪神探」）并确认；用于演示 SDK 的选人事件与选人操作 API。",
    Permissions = ModPermission.ReadGameState | ModPermission.GameActions | ModPermission.FileWrite,
    SdkVersion = "3.2.0")]
