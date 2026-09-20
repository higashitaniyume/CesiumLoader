using CesiumLoader.SDK;

// 相机探针: 只读游戏状态, 不修改任何东西。
[assembly: ModManifest("相机探针", "2.1.1", "CesiumLoader",
    "输出主相机/全部相机的详细信息, 并判断 Cinemachine 接管情况与自由相机可行性",
    Permissions = ModPermission.ReadGameState, SdkVersion = "2.1.1")]
