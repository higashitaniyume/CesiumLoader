using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("自动感谢", "1.2.0", "CesiumLoader",
    "队友帮助后延迟发送原版感谢短语，默认1秒、最多3秒；结算后逐个点赞其他真人玩家，默认间隔500ms。支持独立开关和真实时间冷却，回放/观战不发送。",
    Permissions = ModPermission.ReadGameState | ModPermission.GameActions | ModPermission.FileWrite,
    SdkVersion = "2.8.6")]
