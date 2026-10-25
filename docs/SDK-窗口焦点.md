# SDK-窗口焦点 (WindowFocus)

> 把游戏窗口切换到前台，可选同时播放提示音。热更程序集无法 P/Invoke，实际动作由原生加载器执行。

命名空间：`CesiumLoader.SDK.Windowing`

## 用法

```csharp
using CesiumLoader.SDK.Windowing;

// 只置前
if (WindowFocus.IsAvailable && WindowFocus.RequestForeground())
{
    // 请求已落盘，加载器会在约 100ms 内执行
}

// 置前 + 提示音（绝对路径，WAV）
if (WindowFocus.SupportsSound && WindowFocus.RequestForeground(@"C:\...\sounds\your-turn.wav"))
{
}
```

| 成员 | 说明 |
|---|---|
| `bool IsAvailable` | 加载器是否提供窗口置前通道（读取 `state.txt` 的 `available=1`） |
| `bool SupportsSound` | 加载器是否支持提示音（`state.txt` 的 `audio`，2.7.1 起为 `wav`） |
| `bool RequestForeground()` | 写入置前请求；返回 `true` 只表示请求已落盘，不保证系统一定允许激活 |
| `bool RequestForeground(string soundFile)` | 置前的同时播放提示音；路径必须是绝对路径 |
| `string Directory` | 当前使用的控制目录 |
| `string UnavailableReason` | 通道不可用/写请求失败的原因，可用时为 `null` |
| `string Describe()` | 诊断描述，适合写日志 |

`RequestForeground(...)` 不抛异常：通道不可用、目录或文件写入失败时返回 `false`，原因可通过 `UnavailableReason` 或 `Describe()` 查看。

## 提示音

- 只在加载器**真正处理这次请求**时播放：窗口已经在前台时不抢焦点，也不播放。
- 系统拒绝置前（前台是全屏程序或提权窗口）时仍会播放 —— 这时提示音就是唯一的提醒。
- 格式：**WAV**，建议 16-bit PCM、44.1 kHz、单声道、不超过 2 秒。加载器用 Windows `PlaySound` 异步播放，不阻塞游戏线程。
- 上限 16 MB。文件不存在、扩展名不是 `.wav` 或文件过大时只写加载器日志，置前不受影响。
- 不经过游戏的 Wwise 音频：游戏内音量/静音不影响它，但 Windows 音量合成器里本游戏的音量仍然有效。
- 新请求会打断上一段提示音，不会叠音。
- 相对路径、含换行/回车的路径不会被写入请求（SDK 直接丢弃），结果等于“只置前”。

## 通道

| 文件 | 方向 | 内容 |
|---|---|---|
| `<CESIUM_WINDOW_DIR>\request.txt` | mod → 加载器 | 第 1 行是新的十进制令牌（内容变化即触发）；第 2 行可选，提示音绝对路径（UTF-8） |
| `<CESIUM_WINDOW_DIR>\state.txt` | 加载器 → mod | `version=<加载器版本>`、`available=0/1`、`audio=wav` |

`CESIUM_WINDOW_DIR` 默认为 `<游戏目录>\AstralParty_ModLoader\window`，加载器同时镜像到 `%LocalAppData%\AstralParty_ModLoader\window`（SDK 会自动尝试两处）。

加载器每 100ms 检查一次请求文件，只处理内容变化；启动时把 `request.txt` 重置为 `0`，上一次会话的残留请求不会在下次启动时执行。游戏窗口已经在前台时忽略请求；最小化的窗口先恢复；系统前台锁定拒绝置前时闪烁任务栏（提示音照常播放）。

## 行为与限制

- 置前不影响游戏逻辑，也不修改窗口的置顶属性。
- 游戏窗口被其他程序遮挡、最小化或位于其他虚拟桌面时都可尝试激活。
- Windows 的前台锁定可能拒绝切换（例如前台是全屏程序或提权窗口），此时只能闪烁任务栏，无法保证 100% 成功。
- 加载器配置 `windowControlEnabled=false` 时通道关闭，`IsAvailable` 与 `SupportsSound` 均为 `false`；其它功能不受影响。
- 旧版加载器（2.7.0 及更早）没有提示音：请求只写第一行，`SupportsSound` 为 `false`，置前照常。

## 相关模组

内置 [TurnFocusMod](../mods/TurnFocusMod/README.md) 在轮到自己回合、选择筹码、战斗出牌、攻击骰或防御选择时调用本接口；匹配成功时，以及匹配成功开房后其他玩家发送“准备”等快捷消息时也会调用。提示音默认关闭，模组配置里指定 `soundFile` 后才播放。
