using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("崩溃探针", "1.0.0", "CesiumLoader",
    "崩溃转储验证工具：打开设置页或按热键后，按配置故意触发原生访问冲突 / 栈溢出 / 死锁，用来验证加载器的 minidump 与日志通路。会真的让游戏崩溃或无响应，只用于测试环境。",
    Permissions = ModPermission.Debug, SdkVersion = "2.8.6")]
