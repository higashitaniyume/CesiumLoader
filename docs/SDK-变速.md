# SDK-变速 (SpeedHack)

> 游戏时间流速控制接口。mod 可调用 `SpeedHack.SetSpeed()` 变速，配合热键轮询实现 CheatEngine 式变速。
> 实现借鉴 [speedhack-rs](https://github.com/Hirtol/speedhack-rs)（CheatEngine speedhack 的开源复刻）。

## ⚠️ 警告

- 变速影响游戏感知的**所有**时间：动画、演出、回合计时、网络超时。
- **联机对局慎用**：服务器是权威时钟，本地时间戳与服务器不一致会被检测到异常节奏，有断线/封号风险。
- 建议仅用于单机练习、看回放、本地观感加速。
- 倍率范围 `(0, 100]`，`1.0` = 正常。负倍率/0 不支持。

## 原理

| 层 | 做什么 |
|---|---|
| 原生加载器 (version.dll) | 进程启动后用 **MinHook** inline hook 4 个系统时间函数，按倍率缩放返回值 |
| SDK `SpeedHack` 类 | P/Invoke 调加载器的 `ap_speed_set` / `ap_speed_get` / `ap_speed_active` 导出 |
| mod | 调用 `SpeedHack.SetSpeed()`，用 SDK 的 `InputService` 轮询热键（内置 `SpeedHackMod` 就是这么做的） |

被 hook 的函数（与 CheatEngine/speedhack-rs 一致）：

| 函数 | 用途 |
|---|---|
| `GetTickCount` / `GetTickCount64` | 系统运行时间 |
| `timeGetTime` | 多媒体计时 |
| `QueryPerformanceCounter` | **Unity 计时核心**（`Time.time` / `deltaTime` 基于它） |

缩放算法（与 speedhack-rs `TimeState` 等价）：每个 API 记录 `basetime`（设速时的真实时间）和 `offset`（当时的虚拟时间），
`虚拟 = offset + (real - basetime) * speed`。切换倍率时重设 basetime/offset，保证时间连续不跳变。

## API

```csharp
using CesiumLoader.SDK;

// 变速引擎是否可用(加载器是否安装了 hook)
bool ok = SpeedHack.IsAvailable;

// 设置倍率: 2.0 = 2 倍速, 0.5 = 半速, 1.0 = 正常
bool success = SpeedHack.SetSpeed(2.0);

// 当前倍率
double cur = SpeedHack.Speed;

// 恢复 1.0
SpeedHack.Reset();
```

`SetSpeed` 返回 `false` 的情况：引擎不可用、加载器没有相应导出、倍率为 `NaN`/`≤0`/`>100`（不抛异常）。

## 变速是加载器内置功能

变速不依赖任何 mod：`doorstop_config.json` 的 `speedhackBaseSpeed` 控制基础倍率
（`1.0`=正常，`2.0`=全程 2 倍速），游戏启动即应用并全程保持。
AstralParty.Toys 的模组页面提供可视化开关（写入该字段）。

**已经做好的热键 mod**：随包内置的 `SpeedHackMod`（[文档](mod-SpeedHackMod.md)）——
`Delete` 开关、`Alt+=` / `Alt+-` 实时调倍率、调完自动写回配置。
想自己写一个的话，下面是最小示例。

## 纯计算辅助 (热键调倍率用)

`SpeedHack.StepSpeed` / `SpeedHack.ClampSpeed` 不碰引擎，只做"方向 × 步长 + 夹紧 + 规整小数位"，
便于离线测试与复用（内置 SpeedHackMod 就是用它算倍率的）：

```csharp
// 从当前倍率按一次热键: 2.0 -> 2.5 (+0.5); 返回值与入参相同 = 已到极限
double next = SpeedHack.StepSpeed(current: 2.0, delta: +1, step: 0.5, min: 0.1, max: 10.0);

// 脏配置也安全: step <= 0 / NaN / Inf 退回 DefaultSpeedStep(0.5),
// min/max 非法或写反自动纠正, NaN 倍率当 1.0
double safe = SpeedHack.StepSpeed(2.0, +1, step: 0.0);
```

常量：`MinSpeed`(0.1) / `MaxSpeed`(10) / `DefaultSpeedStep`(0.5)。

> `SetSpeed` 会显式拒绝 `NaN`。`NaN` 与任何数比较都是 false，只靠 `<=0 / >100` 拦不住，
> 而 `NaN` 倍率会让游戏虚拟时间彻底坏掉 —— 自己直接 P/Invoke `ap_speed_set` 时也要注意这点。


mod 也可用 SDK API 编程控制（配合热键轮询实现 CheatEngine 式变速）：

```csharp
using CesiumLoader.SDK;
using UnityEngine;

[ModManifest("我的变速mod", "1.0.0", "作者", "描述",
    Permissions = ModPermission.SpeedHack, SdkVersion = "2.1.3")]
public static class ModEntry
{
    public static void Main()
    {
        if (!SpeedHack.IsAvailable) return;    // 引擎装不上就什么都不做
        ModBase.Run(new MySpeedMod(), 10000);  // 不碰游戏单例, 可以早点接管热键
    }
}

public sealed class MySpeedMod : ModBase
{
    private double _speed = 2.0;

    public override void OnUpdate()
    {
        // 热键用 SDK 的 InputService(键盘可靠; 本作拿不到鼠标输入)
        if (InputService.IsKeyPressed(KeyCode.Delete))
            SpeedHack.SetSpeed(_speed > 1.0 ? 1.0 : _speed);      // 开关

        bool alt = InputService.IsKeyHeld(KeyCode.LeftAlt) || InputService.IsKeyHeld(KeyCode.RightAlt);
        if (alt && InputService.IsKeyHeld(KeyCode.Equals))
        {
            _speed = SpeedHack.StepSpeed(_speed, +1, 0.5, 0.5, 4.0);  // 夹紧 + 规整小数位
            SpeedHack.SetSpeed(_speed);
        }
    }
}
```

> 版本 ≥ 2.1.3 的 SDK 里，`ModBase.Run(mod, delayMs)` 传的延迟可以小于默认的 30 秒：
> 30 秒是为了避开"启动早期访问游戏单例"的崩溃窗口，只碰加载器导出/Input 的 mod 不需要等那么久。

### mod 配置示例 (mods\MySpeedMod\config.json)

```json5
{
  "speed": 2.0,             // 开启时使用的倍率(热键调整后自动更新)
  "speedStep": 0.5,         // 每次调整的步长
  "minSpeed": 0.1,          // 可调下限
  "maxSpeed": 10.0,         // 可调上限
  "toggleKey": "Delete",    // 开关键
  "speedUpKey": "Equals",   // 加速键(按住 Alt)
  "speedDownKey": "Minus",  // 减速键(按住 Alt)
  "useNumpadKeys": true,    // 小键盘 +/- 也认
  "repeatInterval": 0.15,   // 按住连调的间隔(秒), 0 = 只调一次
  "rememberSpeed": true,    // 写回配置
  "notify": true            // 屏幕提示
}
```

> `ModConfig` 自动落在 `mods\{ModId}\config.json`（与 mod 的 DLL 同文件夹），
> 不要放到旧的 `AstralParty_ModLoader\configs\` 下 —— 那是已废弃的旧布局。

## 与外部变速工具的关系

- **不能**与同样劫持 `version.dll` 的工具（如 speedhack-rs）同时使用——Windows 按模块名只加载一个 `version.dll`。
- 本功能把变速做进了加载器本身（MinHook + `ap_speed_*` 导出），取代外部工具，且支持 mod 编程控制。
- 若外部工具支持改名加载（speedhack-rs 可改 `[lib] name` 重编译），也可由本加载器 `LoadLibrary` 加载，但倍率/热键由它自己的配置控制，不走 SDK 接口。
