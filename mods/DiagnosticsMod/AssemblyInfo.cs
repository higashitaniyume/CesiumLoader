using CesiumLoader.SDK.Manifests;

// 诊断工具: 只读。
[assembly: ModManifest("诊断工具", "2.3.0", "CesiumLoader",
    "F10 全量诊断转储, F11 相机信息, F12 场景信息 (输出到本次启动的 logs 日志文件)",
    Permissions = ModPermission.ReadGameState, SdkVersion = "2.3.0")]
