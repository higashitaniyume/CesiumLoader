using CesiumLoader.SDK.Manifests;

// 自由相机: 滚轮缩放(沿当前视线前后移动, 视角完全不变), F1 恢复原来的视角; 锁定镜头默认关闭, 需手动设置 autoFollow=false 启用。
[assembly: ModManifest("自由相机", "2.4.5", "CesiumLoader",
    "滚轮缩放: 相机沿当前视线前后移动(朝向/俯角完全不变, 向前滚=拉近/放大, 向后滚=拉远/看更多); F1(可改键)恢复原来的视角。锁定镜头默认不开启; 手动把 config.json 里的 autoFollow 改成 false 后, 角色移动、切换角色、回合开始、刷怪、开大/战斗演出等游戏自动运镜都不会移动镜头(演出用的独立相机冻结在开场机位; 回合过渡/占位相机也不会释放锁定; 玩家拖拽/按键平移自由相机仍按游戏原样)。滚轮缩放只在滚轮后才临时覆盖相机位置, 复原/回到原位/切场景都会完整交还",
    Permissions = ModPermission.ReadGameState, SdkVersion = "2.5.2")]
