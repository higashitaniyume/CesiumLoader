using CesiumLoader.SDK;

// 自由相机: 会临时接管主相机, 退出时完整还原。
[assembly: ModManifest("自由相机", "2.0.0", "CesiumLoader",
    "F1 开关自由相机; WASD 移动, Q/E 升降, 鼠标转向, Shift 加速, Ctrl 减速, 滚轮调 FOV, F2 复位",
    Permissions = ModPermission.ReadGameState, SdkVersion = "2.0.0")]
