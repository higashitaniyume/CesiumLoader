# SDK 生命周期与日志

命名空间：`CesiumLoader.SDK.Mods`（生命周期）；`CesiumLoader.SDK.Logging`（日志）

## ModBase — mod 生命周期基座

```csharp
public static class ModBase
{
    public static void Run(Action init, Action tick, int delayMs = 30000, string tag = "MOD");
}
```

| 参数 | 说明 |
|---|---|
| `init` | 初始化回调（订阅事件/读配置），可为 `null`。在启动门通过后执行一次 |
| `tick` | 每秒轮询回调，可为 `null`。`init` 之后无限循环执行 |
| `delayMs` | **启动上限**，默认 **30000ms**。游戏就绪即初始化，最坏才等满这么久（兜底：启动早期访问 `NetManager`/`UIManager` 等单例触发 0x80000003） |
| `tag` | 日志来源标签，写入日志的 `[tag]` 前缀 |

### 行为细节

- `Run` 内部是 `UniTaskVoid` 异步循环（`Forget()` 即发即弃），不阻塞加载线程。
- `init` 抛异常 → 记录 `init 异常`，**继续**进入 tick 循环。
- `tick` 每次抛异常 → 记录 `tick 异常: {Message}`，**循环不中断**。
- 循环外层 catch → 记录 `运行循环异常`。

### 启动门（为什么不再固定等 30 秒）

mod 由加载器在 HybridCLR 热更就绪时调用 `Main()`，此时游戏自己还在启动。旧实现固定等 30 秒再初始化，等于用挂钟赌"游戏已经启动完"。

现在是**等待上限 + 就绪判定**：

| 情况 | 行为 |
|---|---|
| 判定到游戏就绪 | 立刻初始化（再多等 `SettleMs` 稳定期，默认 1000ms） |
| 到 `delayMs` 仍未就绪 | 按旧行为在该时刻初始化，并在日志里记一条警告 |
| `delayMs <= 0` | 不等待 |

- 就绪 = 已经进入可玩场景（`Home`/`Battle`/`SinglePlayer`/`SelectMusic`/`Music`，不是 `None`/`Login`）**且**已经有一个 UI 面板打开。判定只读 `hasInstance` 与自动属性，**不会**触发单例的懒构造，所以探测本身没有启动早期崩溃风险。
- 任何 mod 都不会比改动前更晚初始化：就绪只会让初始化提前，不会延后。
- 就绪结果在进程内锁存，后启动的 mod 直接跳过等待；日志里有一条 `[STARTUP] 游戏已就绪: 启动门等待 Nms` 记录实际等待时长。
- 关闭方式：环境变量 `CESIUM_MOD_BOOT_GATE=0`（或 `false`/`off`），或运行时 `CesiumLoader.SDK.Mods.StartupGate.Enabled = false`；关闭后恢复"固定等待 `delayMs`"的旧行为。`StartupGate.PollIntervalMs`（默认 100）与 `StartupGate.SettleMs`（默认 1000）也可运行时调整。

> `delayMs` 是**游戏时间**（`UniTask.Delay` 口径，受变速影响），所以基础倍率 2x 时 30 秒上限对应约 15 秒墙钟。

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
        GameEvents.EnsureHooked();  // 保持事件挂钩(可选: StartAutoHook 已内部维持)
        // 其他每秒轮询逻辑
    }
}
```

> `GameEvents.EnsureHooked()` 里，战斗相关的回调只在“已经进入战斗”时才挂钩，匹配/选人相关回调则由 `EnsureRoomHooked()` 单独挂钩；所有单例读取都先过 `hasInstance`，不会在游戏启动早期强行构造单例。所以它不需要前置等待，`StartAutoHook()` 从第一帧就可以开始轮询。

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

- 写入加载器**本次启动**的 activity 日志：环境变量 `CESIUM_ACTIVITY_LOG_FILE`（完整路径，形如
  `<logs>\activity-mod-<yyyyMMdd>-<HHmmss>-<pid>.log`）→ 未设置时回退 `CESIUM_LOG_DIR\activity-mod.log`
  → 两个都没有时回退 `%LocalAppData%\AstralParty_ModLoader\logs\activity-mod.log`。
- 所有 mod 共用**同一个文件**（加载器只转发这一个文件到控制台窗口）。
- **每次启动一套日志文件**：引导日志 / activity 日志 / 错误日志三个文件共用同一个会话标识
  `<yyyyMMdd>-<HHmmss>-<pid>`，所以上一次启动的 mod 日志不会和这一次交错在一起；
  排查时先按名字（或修改时间）挑出对应那次启动的文件。
- 每行格式：`[HH:mm:ss.fff] [级别] [tag] 内容`，例如：

```
[22:28:56.443] [INF] [ActivityLog] [用牌] 企鹅鹅大魔王 (我) 使用了 攻击(中)(10001) (剩3张)
```

级别标签：`DBG` / `INF` / `WRN` / `ERR`。转发到控制台时按级别着色：`DBG` 暗灰、`INF` 默认灰白、
`WRN` 黄色、`ERR`/`FTL` 红色，时间戳与级别保持暗灰；日志文件里始终是纯文本。

### 级别过滤

- 环境变量 `CESIUM_LOG_LEVEL`（`Debug`/`Info`/`Warn`/`Error`，不区分大小写）控制最小显示级别，默认 `Info`（Debug 被过滤）。
- `Init()` 幂等，可多次调用；`Write`/`Info`/`Warn`/`Error`/`Debug` 内部自动初始化。
- 所有写日志操作带 try/catch，日志失败不影响 mod 运行。
