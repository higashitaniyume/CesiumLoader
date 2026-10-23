# SDK-窗口焦点 (WindowFocus)

> 把游戏窗口切换到前台。热更程序集无法 P/Invoke，实际置前由原生加载器执行。

命名空间：`CesiumLoader.SDK.Windowing`

## 用法

```csharp
using CesiumLoader.SDK.Windowing;

if (WindowFocus.IsAvailable && WindowFocus.RequestForeground())
{
    // 请求已落盘，加载器会在约 100ms 内执行
}
```

| 成员 | 说明 |
|---|---|
| `bool IsAvailable` | 加载器是否提供窗口置前通道（读取 `state.txt` 的 `available=1`） |
| `bool RequestForeground()` | 写入置前请求；返回 `true` 只表示请求已落盘，不保证系统一定允许激活 |
| `string Directory` | 当前使用的控制目录 |
| `string UnavailableReason` | 通道不可用/写请求失败的原因，可用时为 `null` |
| `string Describe()` | 诊断描述，适合写日志 |

`RequestForeground()` 不抛异常：通道不可用、目录或文件写入失败时返回 `false`，原因可通过 `UnavailableReason` 或 `Describe()` 查看。

## 通道

| 文件 | 方向 | 内容 |
|---|---|---|
| `<CESIUM_WINDOW_DIR>\request.txt` | mod → 加载器 | 每次请求写入一个新的十进制令牌，内容变化即触发 |
| `<CESIUM_WINDOW_DIR>\state.txt` | 加载器 → mod | `version=<加载器版本>`、`available=0/1` |

`CESIUM_WINDOW_DIR` 默认为 `<游戏目录>\AstralParty_ModLoader\window`，加载器同时镜像到 `%LocalAppData%\AstralParty_ModLoader\window`（SDK 会自动尝试两处）。

加载器每 100ms 检查一次请求文件，只处理内容变化；启动时把 `request.txt` 重置为 `0`，上一次会话的残留请求不会在下次启动时执行。游戏窗口已经在前台时忽略请求；最小化的窗口先恢复；系统前台锁定拒绝置前时闪烁任务栏。

## 行为与限制

- 置前不影响游戏逻辑，也不修改窗口的置顶属性。
- 游戏窗口被其他程序遮挡、最小化或位于其他虚拟桌面时都可尝试激活。
- Windows 的前台锁定可能拒绝切换（例如前台是全屏程序或提权窗口），此时只能闪烁任务栏，无法保证 100% 成功。
- 加载器配置 `windowControlEnabled=false` 时通道关闭，`IsAvailable` 为 `false`；其它功能不受影响。

## 相关模组

内置 [TurnFocusMod](../mods/TurnFocusMod/README.md) 在轮到自己回合、选择筹码、战斗出牌、攻击骰或防御选择时调用本接口。
