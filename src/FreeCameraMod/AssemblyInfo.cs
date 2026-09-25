using CesiumLoader.SDK;

// 自由相机: 滚轮缩放(沿当前视线前后移动, 视角完全不变), F1 恢复原来的视角; 进游戏不接管任何东西。
[assembly: ModManifest("自由相机", "2.2.1", "CesiumLoader",
    "滚轮缩放: 相机沿当前视线前后移动(朝向/俯角完全不变, 向前滚=拉近/放大, 向后滚=拉远/看更多); F1(可改键)恢复原来的视角。进游戏不接管任何东西, 只在你滚轮后才临时覆盖相机位置, 复原/回到原位/切场景都会完整交还",
    Permissions = ModPermission.ReadGameState, SdkVersion = "2.2.1")]
