using CesiumLoader.SDK;

// 自由相机: 默认"跟随抬高"俯瞰(保留游戏自己的视角操作), 退出时还原位姿/镜头参数。
[assembly: ModManifest("自由相机", "2.1.2", "CesiumLoader",
    "F1 开关俯瞰视角(默认跟随抬高, 游戏自带的鼠标/键盘操作照常可用); Ctrl+= / Ctrl+- 游戏内实时调高度; config.json 里 mode=preset 可改为固定机位",
    Permissions = ModPermission.ReadGameState, SdkVersion = "2.1.2")]
