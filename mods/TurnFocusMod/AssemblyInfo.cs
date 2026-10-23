using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("回合置前", "1.1.0", "CesiumLoader",
    "轮到自己选择筹码、战斗出牌、攻击或防御时把游戏窗口切换到前台，窗口已在前台时不打扰。",
    Permissions = ModPermission.ReadGameState | ModPermission.FileSystem,
    SdkVersion = "2.6.0")]
