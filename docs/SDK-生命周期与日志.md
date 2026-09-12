# SDK 生命周期与日志

命名空间：`CesiumLoader.SDK`

## ModBase — mod 生命周期基座

```csharp
public static class ModBase
{
    public static void Run(Action init, Action tick, int delayMs = 30000, string tag = "MOD");
}
```

| 参数 | 说明 |
|---|---|
| `init` | 初始化回调（订阅事件/读配置），可为 `null`。在 `delayMs` 之后执行一次 |
| `tick` | 每秒轮询回调，可为 `null`。`init` 之后无限循环执行 |
| `delayMs` | 启动延迟，默认 **30000ms**。避开游戏启动早期崩溃窗口（访问 NetManager/UIManager 等单例触发 0x80000003） |
| `tag` | 日志来源标签，写入日志的 `[tag]` 前缀 |

### 行为细节

- `Run` 内部是 `UniTaskVoid` 异步循环（`Forget()` 即发即弃），不阻塞加载线程。
- `init` 抛异常 → 记录 `init 异常`，**继续**进入 tick 循环。
- `tick` 每次抛异常 → 记录 `tick 异常: {Message}`，**循环不中断**。
- 循环外层 catch → 记录 `运行循环异常`。

### 典型用法

```csharp
public static class ModEntry
{
    public static void Main()
    {
        ModBase.Run(OnInit, OnTick, tag: "MyMod");
    }

    static void OnInit()
    {
        GameEvents.CardUsed += (pid, cardId, remain) => { /* ... */ };
    }

    static void OnTick()
    {
        GameEvents.EnsureHooked();  // 保持事件挂钩
        // 其他每秒轮询逻辑
    }
}
```

## SdkLog — 日志

```csharp
public enum SdkLogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

public static class SdkLog
{
    public static void Init();
    public static void Write(string line);                       // 默认 Info, tag="SDK"
    public static void Write(string tag, string line);           // 默认 Info
    public static void Debug(string tag, string line);
    public static void Info(string tag, string line);
    public static void Warn(string tag, string line);
    public static void Error(string tag, string line);
}
```

### 输出位置与格式

- 写入 `CESIUM_LOG_DIR\activity-mod.log`（加载器设置；未设置时回退 `%LocalAppData%\AstralParty_ModLoader\logs\activity-mod.log`）。
- 所有 mod 共用**同一个文件**（当前加载器只转发这一个文件到控制台窗口）。
- 每行格式：`[HH:mm:ss.fff] [级别] [tag] 内容`，例如：

```
[22:28:56.443] [INF] [ActivityLog] [用牌] 企鹅鹅大魔王 (我) 使用了 攻击(中)(10001) (剩3张)
```

级别标签：`DBG` / `INF` / `WRN` / `ERR`。

### 级别过滤

- 环境变量 `CESIUM_LOG_LEVEL`（`Debug`/`Info`/`Warn`/`Error`，不区分大小写）控制最小显示级别，默认 `Info`（Debug 被过滤）。
- `Init()` 幂等，可多次调用；`Write`/`Info`/`Warn`/`Error`/`Debug` 内部自动初始化。
- 所有写日志操作带 try/catch，日志失败不影响 mod 运行。
