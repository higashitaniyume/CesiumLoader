using CesiumLoader.SDK;

// mod 元数据: 程序集级声明(权威位置, 读取时不触发类型加载, 兼容 HybridCLR)。
// 本 mod 会改写游戏配置的内存态(不改文件、不碰网络协议本身), 所以声明 ModifyGameState。
[assembly: ModManifest("极限难度解锁", "1.0.4", "astralparty-re",
    "清空 FixMap 给『极限』档(难度 Index=4)写的时间窗, 让极限难度重新出现在房间设置/匹配面板里。仅对定义了第 5 档的 3 张 PvE 图(82010 水乡古镇/82007 星趴·梦想号/82008 御魂庆典)生效。v1.0.1: 修掉实机暴露的 MethodNotFind System.DateTimeOffset::FromUnixTimeSeconds(改用纯整数日期换算), 并把补丁顺序提到所有格式化/日志之前。v1.0.2: 加 F9 主动探测 —— 直接发一次 CreateMatchTeamC2S(Pve) 并记录服务器返回的 errId。v1.0.3: 修热键失效 —— 探测键判定被 % 120 节流挡住(IsKeyPressed 只在按下那一帧为真, 按多次也命不中), 现改为每帧检查 + 用 IsKeyHeld 自算下降沿。v1.0.4: 增加只读的『组队匹配』队伍状态监视 —— 玩家自己点匹配建队后, 服务器回传的 MatchDifficulty 会打进日志, 不依赖热键。",
    Permissions = ModPermission.ModifyGameState | ModPermission.ReadGameState,
    SdkVersion = "2.2.0")]
