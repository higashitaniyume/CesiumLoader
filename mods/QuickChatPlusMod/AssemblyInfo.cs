using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("快捷短语扩展", "1.0.0", "CesiumLoader",
    "战斗内快捷短语面板补入 Chat 配置里的全部既有短语（房间等待、结算与其他模式），列表超出屏幕时自动限高并支持滚轮或拖动滚动；文本与发送沿用游戏原版。",
    Permissions = ModPermission.ReadGameState | ModPermission.ModifyGameState | ModPermission.FileWrite,
    SdkVersion = "2.8.6")]
