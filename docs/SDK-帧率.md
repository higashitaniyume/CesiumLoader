# SDK 帧率与垂直同步

命名空间：`CesiumLoader.SDK.Engine`

`UnityFrameRate` 封装 `Application.targetFrameRate` 与 `QualitySettings.vSyncCount`。
所有调用都经过 `UnityCall` 的 ECall 隔离层：Unity 不可用或成员被裁剪时返回 `false` / 默认值，
不抛异常，也不会因为一次读写失败拖垮模组。

```csharp
using CesiumLoader.SDK.Engine;

int current = UnityFrameRate.Target;   // 当前目标帧率(-1 = 不限); 读不到时 0
int vsync   = UnityFrameRate.VSync;    // 0 = 关闭

UnityFrameRate.SetVSync(0);            // 不限帧率的前提: 关闭垂直同步
UnityFrameRate.SetUnlimited();         // 等价于 SetTarget(-1)
UnityFrameRate.SetTarget(144);         // 也可以指定上限
```

| 成员 | 说明 |
|---|---|
| `Unlimited` | 常量 `-1`，表示不限帧率 |
| `MaxCustom` | 常量 `1000`，`SetTarget` 接受的最大上限 |
| `Target` | 当前目标帧率；读取失败返回 `0` |
| `VSync` | 当前垂直同步计数；读取失败返回 `0` |
| `SetTarget(int)` | 写入 `-1` 或 `1–1000`；超出范围或 Unity 不可用返回 `false` |
| `SetUnlimited()` | 写入 `-1`，等价于 `SetTarget(Unlimited)` |
| `SetVSync(int)` | 写入 `0–4`（`0` = 关闭）；超出范围或不可用返回 `false` |
| `Describe()` | 单行状态文本，用于日志 |

## 注意

- 这是**运行时**值，与游戏存档里的画质/帧率设置无关。游戏在启动、切换画质、打开设置面板或退出节能模式时会重新写入自己的值，需要再次施加才会保持。
- 目标帧率只在垂直同步关闭（`VSync == 0`）时生效。`SetUnlimited()` 与 `SetVSync(0)` 一起使用才是不限帧率。
- Unity 只保证 `-1` 表示「不限帧」；正数上限由平台实现，可能有取整或节流误差。
- 必须在主线程调用（与其他 Unity 封装一致）。
