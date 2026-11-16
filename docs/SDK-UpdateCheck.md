# SDK 更新检查（UpdateCheck）

> 相关源码：`Updates/UpdateCheck.cs`、`Updates/UpdateCheckState.cs`；
> 加载器侧实现在 CesiumLoader 仓库的 `loader/CesiumLoader/update/`。
> 内置示例：[UpdateNoticeMod](../mods/UpdateNoticeMod/README.md)。

## 1. 职责划分

| 谁 | 做什么 |
| --- | --- |
| 原生加载器 | 后台线程用 WinHTTP 拉取版本清单（HTTPS），比较版本，把结论写进 `<loader>\update\state.json`，控制台打一行 `[update]` |
| SDK / mod | **只读**那个状态文件，决定要不要提示、怎么提示；需要重新检查或打开浏览器时，发一个本地 UDP 请求给加载器 |

这样切分的原因：

- 热更程序集（HybridCLR）的 BCL 是裁剪过的，`HttpClient` / `WebRequest` / `SslStream` 没有可靠先例；
  WinHTTP 是纯原生能力，不需要游戏提供任何东西；
- 网络失败、超时、镜像回退都留在原生层，mod 侧不会因为网络卡住主线程；
- mod 没装（或被停用）时检查结果照样存在，控制台与状态文件仍可看。

## 2. 状态文件

路径：`<游戏目录>\AstralParty_ModLoader\update\state.json`
（环境变量 `CESIUM_UPDATE_DIR` 优先，SDK 的 `UpdateCheck.StatePath` 已经处理好）。

加载器用「临时文件 + 原子替换」写这个文件，所以读到的永远是一份完整 JSON：

| 字段 | 含义 |
| --- | --- |
| `status` | `update-available` / `up-to-date` / `error` / `disabled` |
| `currentVersion` | 检查时加载器自身的版本（写下的快照） |
| `latestVersion` | 线上最新版本 |
| `releaseTag` / `releaseDate` | Release 标签与发布时间（ISO 8601 UTC） |
| `releaseUrl` | 发布页（打开发布页用） |
| `downloadUrl` / `downloadSha256` / `downloadSize` | 加载器整包 zip 的信息 |
| `releaseNotes` | 清单里的更新说明（通常为空） |
| `source` | `direct` 或 `mirror`（走了镜像） |
| `error` | 失败原因 |
| `checkedAt` / `checkedAtUnix` | 检查时间 |

## 3. 读状态

```csharp
using CesiumLoader.SDK.Updates;

UpdateCheckState state;
if (UpdateCheck.TryGetState(out state) && state != null)
{
    if (state.HasUpdate)
        Log.Info("有新版本 " + state.LatestVersion + "（当前 " + state.CurrentVersion + "）");
    else if (state.IsError)
        Log.Warn("检查失败：" + state.Error);
    else if (state.IsUpToDate)
        Log.Info(state.Describe());          // "已是最新 (3.8.4)"
}
```

- `TryGetState` 是纯文件读，任意线程都能调；文件不存在（加载器没开检查 / 还没写完）返回 `false`。
- `UpdateCheckState` 是**快照**，不会自己刷新；轮询它就能跟上加载器的后台检查结果。
- 便捷属性：`HasUpdate` / `IsUpToDate` / `IsError` / `IsDisabled` / `CheckedAtUtc`，
  以及 `Describe()`（一行摘要）与 `DescribeDetailed()`（带 tag、通道、检查时间）。

## 4. 请求重新检查 / 打开浏览器

```csharp
if (UpdateCheck.RequestCheck()) Log.Info("已请求重新检查");   // 忽略冷却，后台执行
if (!UpdateCheck.OpenReleasePage()) Log.Warn("打不开浏览器，发布页：" + UpdateCheck.FallbackReleasePage);
```

通道是本地 UDP（`127.0.0.1:<CESIUM_UPDATE_PORT>`，每进程一个端口），报文只有两种：

| 报文 | 加载器动作 |
| --- | --- |
| `check` | 忽略冷却，立刻重新检查一次 |
| `open <url>` | 用默认浏览器打开该 https 地址 |

`OpenUrl` 只接受 `https://`、URL 里不能有空格或换行；加载器侧还会再按域名白名单放行
（GitHub 与已配置的镜像）。两个方法都只代表「请求已发出」，结果仍要看状态文件。

## 5. 与配置的关系

加载器的 `doorstop_config.json` 控制自动检查：

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `updateCheckEnabled` | `true` | 是否在启动时自动联网检查 |
| `updateCheckUrl` | GitHub Release 的 `version.json` | 清单地址（字段同名即可换成自己的） |
| `updateCheckMirror` | `auto` | `auto` = 直连失败后走 gh-proxy；`off` = 只直连；其它值当镜像前缀 |
| `updateCheckIntervalHours` | `6` | 两次联网检查的最小间隔；有效期内复用上次结果 |
| `updateCheckTimeoutSec` | `8` | 单条请求超时 |

控制台里 `update` 查看结论、`update check` 强制重查、`update open` 打开发布页。

## 6. 写提示面板

SDK 的 `WidgetService` 提供 `Canvas` / `Panel` / `Text` / `Button` / `Slider`，
足够拼一个居中提示面板（`UpdateNoticeMod` 就是这么做的）：

```csharp
var scope = WidgetService.CreateScope("update-notice");
var canvas = scope.Track(WidgetService.Canvas("UpdateNoticeCanvas", sortingOrder: 3000));
var panel = scope.Track(WidgetService.Panel(canvas.Rect, new Color(0.06f, 0.07f, 0.1f, 0.94f), rounded: true));
WidgetService.Place(panel.Rect, WidgetAnchor.Center, Vector2.zero, new Vector2(640f, 360f));

var open = WidgetService.Button(panel.Rect, new Color(0.24f, 0.5f, 0.82f, 0.95f));
WidgetService.Place(open.Rect, WidgetAnchor.Bottom, new Vector2(-100f, 52f), new Vector2(180f, 44f));
WidgetService.SetOnClick(open.Rect, () => UpdateCheck.OpenReleasePage());
```

约定：

- 坐标是 CanvasScaler 参考分辨率（1920×1080）下的像素；
- 面板本体默认不接收鼠标（不挡游戏点击），按钮才是 `raycastTarget = true`；
- 按钮点击依赖 `EventSystem`，`WidgetService.EnsureEventSystem()` 会在缺失时补一个；
- `WidgetService.Button` / `SetOnClick` 在缺 uGUI 类型时返回 `null` / `false`，
  此时应退回键盘交互（快捷键），而不是让整个面板消失。
