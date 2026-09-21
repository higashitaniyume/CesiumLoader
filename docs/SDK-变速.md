# SDK-变速 (SpeedHack)

> 游戏时间流速控制接口。mod 可调用 `SpeedHack.SetSpeed()` 变速，配合热键轮询实现 CheatEngine 式变速。
> 实现借鉴 [speedhack-rs](https://github.com/Hirtol/speedhack-rs)（CheatEngine speedhack 的开源复刻）。

## ⚠️ 警告

- 变速影响游戏感知的**所有**时间：动画、演出、回合计时、网络超时。
- **不要把倍率调太高**：倍率越高，动画/物理/协程越容易变形，演出与网络节奏也越难对齐。
  实际使用建议不超过 `3.0`（热键上限默认 `10.0`，引擎硬上限 `100`）。
- 倍率范围 `[1.0, 100]`，`1.0` = 正常。**低于 1 倍（减速）被硬性禁止**：请求在加载器层就会被
  忽略（`SDK.SetSpeed` / `ClampSpeed` 也会先拦一次），`minSpeed` 写再小也会被抬到 `1.0`。

## 原理

| 层 | 做什么 |
|---|---|
| 原生加载器 (version.dll) | 进程启动后用 **MinHook** inline hook 4 个系统时间函数，按倍率缩放返回值；监听变速控制文件并回写状态 |
| SDK `SpeedHack` 类 | 读写加载器的**变速控制文件**（`request.txt` / `state.txt`），不依赖 P/Invoke |
| mod | 调用 `SpeedHack.SetSpeed()`，用 SDK 的 `InputService` 轮询热键（内置 `SpeedHackMod` 就是这么做的） |

### 为什么走文件而不是 P/Invoke（重要）

本游戏的热更程序集（经 HybridCLR 加载的 `AstralParty.Runtime` / SDK / mod）在运行时
**无法 P/Invoke**：任何 `[DllImport]` 调用都会抛
`NotSupportManaged2NativeFunctionMethod` —— IL2CPP 只为**构建期已知**的程序集生成
managed→native thunk，热更程序集没有。实测连 kernel32 的 `GetModuleHandleW` 都调不通
（SDK 的 `Il2CppInteropService` 就是因此在日志里报 `IL2CPP 互操作停用`）。

所以加载器与 mod 之间的原生交互一律走**文件 / 环境变量**（日志转发也是同一思路）：

| 文件 | 方向 | 内容 |
|---|---|---|
| `<CESIUM_SPEED_DIR>\request.txt` | mod → 加载器 | 期望倍率，单个十进制数（`1.000` = 恢复正常；**不是**"关闭变速"——hook 常驻不卸载，见下） |
| `<CESIUM_SPEED_DIR>\state.txt` | 加载器 → mod | `version=` / `speed=` / `base=` / `active=` / `hooks=` |

> `1.0` 的语义就是"乘以 1"：加载器的 hook 一旦装上就常驻，`1.0` 时它直接返回真实时间。
> 所以从任何倍率切回 `1.0` 都不涉及卸载/重装 hook，也不会让 `hooks=` 掉到 0
> （`StateFile` 里 `hooks=4` / `active=1` 在 `1.0x` 下保持不变）。
>
> ⚠️ **但 `1.0` 必须照样走"虚拟时间"公式**：虚拟时间 = `offset + (real - basetime) * speed`，
> 切换倍率时 `offset` 被设成"当时的虚拟时间"。早期实现在 `1.0` 时图快，直接 `return real`
> 绕过了 `offset` —— 于是从 2x 切回 1.0x 的瞬间，游戏看到的时钟会**倒退**（倒退量正好是
> 加速期间攒下的那段时间），主线程僵住：画面冻结、声音还在，直到亏空被追平才恢复。
> `1.0` 只是"斜率 1"的普通情形，走公式就是纯平移（`offset + (real - basetime)`，整数运算）。
> 冒烟测试里的「时间连续性」小节专门守这条不变量。

`CESIUM_SPEED_DIR` 默认是 `<游戏目录>\AstralParty_ModLoader\speed`（加载器启动时创建并注入环境变量；
SDK 还会回退到 `CESIUM_LOG_DIR\..\speed`、`%LocalAppData%\AstralParty_ModLoader\speed`）。

时序：加载器常驻线程每 **100ms** 检查一次请求文件，内容变化且合法就应用并回写状态 ——
所以 `SetSpeed()` 是"**请求已发出**即返回 true"，真正生效在 ~100ms 内（可用 `SpeedHack.Speed` 回读）。
加载器启动时会把 `request.txt` 重置为当前倍率（基础倍率优先），
且只在**内容变化**时应用，非法内容一律忽略并记日志（不会崩、不会改坏时间）。
`state.txt` 里的 `hooks=0` 表示一个时间 hook 都没装上 —— 此时 `SpeedHack.IsAvailable` 为 `false`。

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

// 变速引擎是否可用(加载器装了 hook 且支持控制文件通道; 需要加载器 >= 2.1.5)
bool ok = SpeedHack.IsAvailable;

// 请求倍率: 2.0 = 2 倍速, 1.0 = 正常(下限, 合法)
bool accepted = SpeedHack.SetSpeed(2.0);

// 当前倍率 / 加载器的基础倍率(doorstop_config.json 的 speedhackBaseSpeed)
double cur = SpeedHack.Speed;
double baseSpeed = SpeedHack.BaseSpeed;

// 恢复 1.0
SpeedHack.Reset();

// 诊断(写日志用): "变速引擎可用 当前 2x 基础 2x 目录 ..."
string text = SpeedHack.Describe();
```

`SetSpeed` 返回 `false` 的情况：引擎不可用（没有控制文件通道/`hooks=0`）、倍率为
`NaN`/`±Inf`/`< 1.0`（**不准减速**）`/ > 100`、控制文件写不进去（不抛异常）。
返回 `true` 表示**请求已落盘**，加载器在 ~100ms 内应用。

> 热键 mod 请以 `SpeedHack.Speed` 为准显示"当前倍率"，而不是自己记的值 ——
> 加载器可能因为基础倍率、或拒绝非法请求而与你的预期不同。

## 变速是加载器内置功能

变速不依赖任何 mod：`doorstop_config.json` 的 `speedhackBaseSpeed` 控制基础倍率
（`1.0`=正常，`2.0`=全程 2 倍速），游戏启动即应用并全程保持。
AstralParty.Toys 的模组页面提供可视化开关（写入该字段）。

**已经做好的热键 mod**：随包内置的 `SpeedHackMod`（[文档](mod-SpeedHackMod.md)）——
`Delete` 在 `1.0x` 与"刚才的倍率"之间切换（再按一次切回；引擎保持开启，不是"关闭变速"）、
`Alt+=` / `Alt+-` 实时调倍率、调完自动写回配置。
想自己写一个的话，下面是最小示例（示例只演示"设为 1.0x"，不含回切）。

## 纯计算辅助 (热键调倍率用)

`SpeedHack.StepSpeed` / `SpeedHack.ClampSpeed` 不碰引擎，只做"方向 × 步长 + 夹紧 + 规整小数位"，
便于离线测试与复用（内置 SpeedHackMod 就是用它算倍率的）：

```csharp
// 从当前倍率按一次热键: 2.0 -> 2.5 (+0.5); 返回值与入参相同 = 已到极限
double next = SpeedHack.StepSpeed(current: 2.0, delta: +1, step: 0.5, min: 1.0, max: 10.0);

// 脏配置也安全: step <= 0 / NaN / Inf 退回 DefaultSpeedStep(0.5),
// min/max 非法或写反自动纠正, NaN 倍率当 1.0
double safe = SpeedHack.StepSpeed(2.0, +1, step: 0.0);
```

常量：`MinSpeed`(**1.0**，硬下限) / `MaxSpeed`(10) / `HardMaxSpeed`(100，引擎硬上限) / `DefaultSpeedStep`(0.5)。
`ClampSpeed` / `StepSpeed` 传进来的 `min` 小于 `1.0` 一律会被抬到 `1.0` —— 减速在这三层都不可能发生。

> `SetSpeed` 会显式拒绝 `NaN` / `±Inf`。`NaN` 与任何数比较都是 false，只靠 `<=0 / >100` 拦不住，
> 而 `NaN` 倍率会让游戏虚拟时间彻底坏掉 —— 写控制文件时同样要挡住它。


mod 也可用 SDK API 编程控制（配合热键轮询实现 CheatEngine 式变速）：

```csharp
using CesiumLoader.SDK;
using UnityEngine;

[ModManifest("我的变速mod", "1.0.0", "作者", "描述",
    Permissions = ModPermission.SpeedHack, SdkVersion = "2.1.7")]
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
        // Delete = 设为正常倍率(引擎保持开启, 不是"关掉变速")
        if (InputService.IsKeyPressed(KeyCode.Delete))
            SpeedHack.SetSpeed(1.0);

        bool alt = InputService.IsKeyHeld(KeyCode.LeftAlt) || InputService.IsKeyHeld(KeyCode.RightAlt);
        if (alt && InputService.IsKeyHeld(KeyCode.Equals))
        {
            _speed = SpeedHack.StepSpeed(_speed, +1, 0.5, 0.5, 4.0);  // 夹紧 + 规整小数位
            SpeedHack.SetSpeed(_speed);
        }
    }
}
```

> `ModBase.Run(mod, delayMs)` 的延迟可以取比默认 30 秒更小的值：30 秒是为了避开"启动早期
> 访问游戏单例"的崩溃窗口；只碰加载器控制文件 / `InputService` 的 mod 不需要等那么久
> （内置 `SpeedHackMod` 用 10 秒），碰游戏单例的 mod 请保持默认。

### mod 配置示例 (mods\MySpeedMod\config.json)

```json5
{
  "speed": 2.0,             // Alt 调整的记忆倍率(热键调整后自动更新)
  "speedStep": 0.5,         // 每次调整的步长
  "minSpeed": 1.0,          // 可调下限(硬性 ≥1.0: 写更小的值会被抬到 1.0, 不允许减速)
  "maxSpeed": 10.0,         // 可调上限
  "toggleKey": "Delete",    // "设为 1.0x"键
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
- 本功能把变速做进了加载器本身（MinHook + 变速控制文件通道），取代外部工具，且支持 mod 编程控制。
- 加载器仍然导出 `ap_speed_set` / `ap_speed_get` / `ap_speed_active`（原生调用方/外部工具可用），
  但**热更程序集调不到它们**（见上文 P/Invoke 限制），mod 请用 `SpeedHack` 类走文件通道。
- 若外部工具支持改名加载（speedhack-rs 可改 `[lib] name` 重编译），也可由本加载器 `LoadLibrary` 加载，但倍率/热键由它自己的配置控制，不走 SDK 接口。
