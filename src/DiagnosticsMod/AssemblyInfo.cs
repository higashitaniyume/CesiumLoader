using CesiumLoader.SDK;

// 诊断工具: 只读。
[assembly: ModManifest("诊断工具", "2.1.5", "CesiumLoader",
    "F10 全量诊断转储, F11 相机信息, F12 场景信息 (输出到 logs\\cesium-loader.log)",
    Permissions = ModPermission.ReadGameState, SdkVersion = "2.1.5")]
