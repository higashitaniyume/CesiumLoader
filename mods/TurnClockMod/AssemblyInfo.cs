using CesiumLoader.SDK.Manifests;

// 回合时钟: 变速时只把我的倒计时按真实时间走(操作窗口、选人、结算、房间等待、剧情自动播放)。
// 会改写游戏计时器的时间轴字段(内存态), 所以声明 ModifyGameState; 不改全局时间、不发协议包。
[assembly: ModManifest("回合时钟", "1.4.0", "CesiumLoader",
    "变速时只把我的倒计时按真实时间走: 操作窗口、选人界面、结算界面、房间等待与剧情自动播放, 2 倍速下 30 秒窗口仍是 30 秒真实时间; 动画、战斗、别人回合与怪物回合照常加速",
    Permissions = ModPermission.ModifyGameState, SdkVersion = "2.8.6")]
