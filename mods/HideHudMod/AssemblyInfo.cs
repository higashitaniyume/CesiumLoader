using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("隐藏HUD", "1.2.0", "CesiumLoader",
    "长按反引号键（Tab 上方）隐藏战斗 HUD，松开恢复；可改为按一下切换。手牌、怪物信息、头像（或仅队友ID）与其他 HUD 可分别开关。",
    Permissions = ModPermission.ReadGameState | ModPermission.Input | ModPermission.UI,
    SdkVersion = "2.8.6")]
