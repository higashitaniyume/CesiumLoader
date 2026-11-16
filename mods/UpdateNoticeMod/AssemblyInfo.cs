using CesiumLoader.SDK.Manifests;

// 纯 SDK + uGUI 提示面板: 不触碰任何游戏类型, 所以不需要 AstralParty.Runtime 引用,
// 也不区分 Windows/Android(两边都能跑; 加载器没写状态文件时它什么都不做)。
[assembly: ModManifest("加载器更新提示", "1.0.0", "CesiumLoader",
    "加载器发现新版本时在游戏里弹一个面板：显示当前版本与最新版本、打开发布页、忽略此版本或稍后再看。数据来自原生加载器的更新检查（<loader>\\update\\state.json），模组自身不联网。",
    Permissions = ModPermission.UI | ModPermission.Input | ModPermission.FileWrite | ModPermission.FileSystem,
    SdkVersion = "3.8.4", Platforms = ModPlatform.Windows | ModPlatform.Android)]
