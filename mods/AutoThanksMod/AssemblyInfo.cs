using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("自动感谢", "1.0.1", "CesiumLoader",
    "队友治疗你、给你牌或转星币给你时，自动发送原版快捷回复：感谢！支持独立开关和真实时间冷却，回放/观战不发送。",
    Permissions = ModPermission.ReadGameState | ModPermission.GameActions | ModPermission.FileWrite,
    SdkVersion = "2.3.0")]
