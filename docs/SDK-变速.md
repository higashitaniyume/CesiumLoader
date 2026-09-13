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
| mod | 调用 `SpeedHack.SetSpeed()`，自行轮询热键（`GetAsyncKeyState`） |

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

`SetSpeed` 返回 `false` 的情况：引擎不可用、倍率 ≤ 0 或 > 100。

## 变速是加载器内置功能

变速不依赖任何 mod：`doorstop_config.json` 的 `speedhackBaseSpeed` 控制基础倍率
（`1.0`=正常，`2.0`=全程 2 倍速），游戏启动即应用并全程保持。
AstralParty.Toys 的模组页面提供可视化开关（写入该字段）。

mod 也可用 SDK API 编程控制（配合热键轮询实现 CheatEngine 式变速）：

```csharp
[ModManifest("我的变速mod", "1.0.0", "作者", "描述")]
public static class ModEntry
{
    public static void Main()
    {
        if (!SpeedHack.IsAvailable) return;
        ModBase.Run(OnInit, OnTick, tag: "MySpeed");
    }

    static void OnTick()
    {
        // 轮询热键: F1=2x, F2=0.5x, F3=恢复
        if (IsKeyDown("F1")) SpeedHack.SetSpeed(2.0);
        else if (IsKeyDown("F2")) SpeedHack.SetSpeed(0.5);
        else if (IsKeyDown("F3")) SpeedHack.Reset();
    }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    static bool IsKeyDown(string key) { /* 用 GetAsyncKeyState 映射 */ }
}
```

### mod 配置示例 (configs/MySpeedMod.json)

```json5
{
  "Enabled": true,          // 总开关
  "BaseSpeed": 2.0,         // 启用即应用的基础倍率 (全程保持)
  "SpeedUpKey": "F1",       // 加速热键
  "SpeedUpValue": 2.0,      // 加速倍率
  "SlowDownKey": "F2",      // 减速热键
  "SlowDownValue": 0.5,     // 减速倍率
  "ResetKey": "F3",         // 恢复 1x 热键
  "IsToggle": false,        // true=切换模式, false=按住生效
  "ReloadConfigOnTick": false // 每次 tick 重读配置
}
```

## 与外部变速工具的关系

- **不能**与同样劫持 `version.dll` 的工具（如 speedhack-rs）同时使用——Windows 按模块名只加载一个 `version.dll`。
- 本功能把变速做进了加载器本身（MinHook + `ap_speed_*` 导出），取代外部工具，且支持 mod 编程控制。
- 若外部工具支持改名加载（speedhack-rs 可改 `[lib] name` 重编译），也可由本加载器 `LoadLibrary` 加载，但倍率/热键由它自己的配置控制，不走 SDK 接口。
