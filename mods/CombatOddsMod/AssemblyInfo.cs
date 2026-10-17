using CesiumLoader.SDK.Manifests;

// mod 元数据: 程序集级声明(权威位置, 读取时不触发类型加载, 兼容 HybridCLR)。
// 本 mod 只读对局数据 + 登记 UI; 技能 CD 会发 GetHeroInfoC2S(与打开玩家信息面板同一条只读查询),
// 不发送任何对局操作, 故只声明 ReadGameState。
[assembly: ModManifest("战斗胜率助手", "2.3.8", "CesiumLoader",
    "打怪投牌界面实时给出该防/该闪/该打的结论(颜色高亮)与关键数字, 棋盘上显示每个玩家实时攻防并支持悬停详情",
    Permissions = ModPermission.ReadGameState, SdkVersion = "2.3.2")]
