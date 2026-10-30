using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("消息停留时间", "1.0.0", "CesiumLoader",
    "让战斗中的快捷消息和表情按真实时间停留更久；默认 15 秒，不受变速影响，可配置。",
    Permissions = ModPermission.ReadGameState | ModPermission.ModifyGameState,
    SdkVersion = "2.8.6")]
