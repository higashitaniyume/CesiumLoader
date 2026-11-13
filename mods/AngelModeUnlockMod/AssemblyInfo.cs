using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("天使模式解锁", "1.0.1", "CesiumLoader",
    "解除审核渠道对天使模式(和谐/SFW)开关的封锁：本地清掉 RemoteConfig 的审核标记，并把设置页被删减版 UI 包藏起来的天使模式开关恢复出来，可像 Steam 国服一样自由切换。",
    Permissions = ModPermission.ReadGameState | ModPermission.FileWrite | ModPermission.ModifyGameState,
    SdkVersion = "3.6.0")]
