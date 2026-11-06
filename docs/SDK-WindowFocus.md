# SDK-窗口焦点 (WindowFocus)

> 把游戏窗口切换到前台，可选同时播放提示音。热更程序集无法 P/Invoke，实际动作由原生加载器执行。

命名空间：`CesiumLoader.SDK.Windowing`

## 用法

```csharp
using CesiumLoader.SDK.Windowing;

// 只置前
if (WindowFocus.IsAvailable && WindowFocus.RequestForeground())
{
    // 请求已发出，加载器会在收到后执行
}

// 置前 + 提示音（绝对路径，WAV）
if (WindowFocus.SupportsSound && WindowFocus.RequestForeground(@"C:\...\sounds\your-turn.wav"))
{
}
```

| 成员 | 说明 |
|---|---|
| `bool IsAvailable` | 加载器是否提供窗口置前通道（同进程 `CESIUM_FOCUS_PORT` 环境变量存在且有效） |
| `bool SupportsSound` | 通道就绪时即支持 WAV（UDP 版本不依赖 state.txt） |
| `bool RequestForeground()` | 发送置前请求；返回 `true` 只表示 datagram 已发出，不保证系统一定允许激活 |
| `bool RequestForeground(string soundFile)` | 置前的同时播放提示音；路径必须是绝对路径；默认只在窗口不在前台时播放 |
| `bool RequestForeground(string soundFile, bool alwaysPlaySound)` | 同上，`alwaysPlaySound=true` 时每次请求都播放（窗口在前台也响） |
| `string Directory` | 遗留诊断值（窗口目录路径）；UDP 版传输不依赖它 |
| `string UnavailableReason` | 通道不可用/发送失败的原因，可用时为 `null` |
| `string Describe()` | 诊断描述，适合写日志（含 `127.0.0.1:<port>`） |

`RequestForeground(...)` 不抛异常：通道不可用、UDP 发送失败时返回 `false`，原因可通过 `UnavailableReason` 或 `Describe()` 查看。

## 提示音

- 默认只在游戏窗口**不在前台**时播放（这时置前才是真正需要的提醒）；`RequestForeground(path, true)` 则每次请求都播放一次，窗口在前台也响。
- 系统拒绝置前（前台是全屏程序或提权窗口）时仍会播放 —— 这时提示音就是唯一的提醒。
- 格式：**WAV**，建议 16-bit PCM、44.1 kHz、单声道、不超过 2 秒。加载器用 Windows `PlaySound` 异步播放，不阻塞游戏线程。
- 上限 16 MB。文件不存在、扩展名不是 `.wav` 或文件过大时只写加载器日志，置前不受影响。
- 不经过游戏的 Wwise 音频：游戏内音量/静音不影响它，但 Windows 音量合成器里本游戏的音量仍然有效。
- 新请求会打断上一段提示音，不会叠音。
- 相对路径、含换行/回车的路径不会被写入请求（SDK 直接丢弃），结果等于“只置前”。

## 通道

采用**本机 UDP（127.0.0.1，每进程独立端口）**：

| 内容 | 方向 | 说明 |
|---|---|---|
| `127.0.0.1:<CESIUM_FOCUS_PORT>` | mod → 加载器 | 无格式令牌/音效/always 的 UDP datagram（与旧版一致的行协议） |
| 端口 | 加载器 → SDK | 加载器 `bind(0)` 后用 `getsockname` 发现端口，再写入同进程环境变量 `CESIUM_FOCUS_PORT` |

- 加载器在 `127.0.0.1` 上绑定临时端口（每进程一个），多开时不会抢同一个文件通道。
- 只绑定回环地址，不向 LAN 暴露。
- 多开隔离来自“内核只投递给这个端口的 socket”，**不再共享 `request.txt` / `state.txt`**。

旧版（≤3.2.x）的文件通道（`<CESIUM_WINDOW_DIR>\request.txt` / `state.txt`）已废弃：新版加载器不再读写它，遗留文件可以保留或删除，不影响新通道。

## 行为与限制

- 置前不影响游戏逻辑，也不修改窗口的置顶属性。
- 游戏窗口被其他程序遮挡、最小化或位于其他虚拟桌面时都可尝试激活。
- Windows 的前台锁定可能拒绝切换（例如前台是全屏程序或提权窗口），此时只能闪烁任务栏，无法保证 100% 成功。
- 加载器配置 `windowControlEnabled=false` 时通道关闭，`IsAvailable` 与 `SupportsSound` 均为 `false`；其它功能不受影响。
- UDP 不提供送达确认；失败时窗口不会切换，任务栏闪烁策略仍按系统允许的最大程度执行。
- 旧版加载器/旧版 SDK 不通用：必须同时部署新的原生加载器与 SDK，否则通道不可用。

## 部署提示

- 任意一次重新部署 `version.dll` / `CesiumLoader.SDK.dll` 时，**先备份再覆盖** `<游戏目录>\AstralParty_ModLoader\doorstop_config.json`，否则会丢失已写入的 `profile` / `consoleEnabled` 等用户设置。
- 建议始终通过 `tools/deploy.ps1` 部署：它会先备份 `version.dll` 和 `doorstop_config.json`；如需手动复制，请先自行备份。
- 已验证的部署顺序：备份 → 复制 `version.dll` / SDK → 启动 → 看日志中的 `[windowctl] UDP channel ready` 及通道端口。

## 相关模组

内置 [TurnFocusMod](../mods/TurnFocusMod/README.md) 在轮到自己回合、选择筹码、战斗出牌、攻击骰或防御选择时调用本接口；匹配成功时、收到房间邀请（右上角出现邀请按钮）时，以及匹配成功开房后其他玩家发送“准备”等快捷消息时也会调用。提示音默认关闭，模组配置里指定 `soundFile` 后才播放。
