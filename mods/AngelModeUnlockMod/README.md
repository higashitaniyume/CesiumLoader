# 天使模式解锁（AngelModeUnlockMod）

解除审核渠道（TapTap 等）对《吉星派对》「天使模式 / SFW」开关的封锁，让设置页像 Steam 国服一样可以自由切换。

## 功能

- **恢复设置页开关**：部分渠道的界面资源把「设置 → 功能」里的天使模式开关藏了起来（编进隐藏分组、停在审核页、文案被替换）。本模组在运行时把这一行恢复出来，显示为「天使模式」，可以直接点击切换。
- **清除审核标记**：把 `RemoteConfig` 里的 `IsAuditMode` / `IsAngelMode` 清为 `false`，并在运行期间保持（重新拉取配置或重新登录后再清一次）。审核模式下被一并隐藏的平台信息、联动内容等也会按非审核行为显示。
- **可选强制固定**：不想手动切换时，可以直接固定成开或关。

## 使用

1. 安装 CesiumLoader 后启动游戏。
2. 打开「设置 → 功能」，天使模式开关会出现在第一行右侧。
3. 点击切换，游戏会按原生流程保存并同步。

切换立刻生效：立绘、卡面、抽取动画等会按当前模式重新加载。

## 配置

`mods/AngelModeUnlockMod/config.json`，改完保存即可热重载：

| 字段 | 默认 | 说明 |
|---|---|---|
| `enabled` | `true` | 总开关，关闭后模组完全不介入。 |
| `unlockAuditMode` | `true` | 清除 `RemoteConfig.IsAuditMode`。 |
| `unlockAngelMode` | `true` | 清除 `RemoteConfig.IsAngelMode`。 |
| `restoreSwitch` | `true` | 恢复设置页开关行。关掉后只清除标记，不碰界面。 |
| `switchTitle` | `""` | 开关标题；留空按界面语言自动取「天使模式」。 |
| `angelMode` | `"keep"` | `keep` 不干预；`sfw` 强制开启；`unsfw` 强制关闭。 |

## 兼容性

- 适用于 3.3.0 版本的 TapTap 渠道客户端，同样兼容 Steam 国服（在开关本来就正常的客户端上是空操作）。
- 需要 CesiumLoader 3.6.0 及以上（开发与验证使用 3.7.x SDK）。
- 只改本地客户端状态；服务器对天使模式同步的接受与否由游戏自身流程决定。

## 许可

AGPL-3.0
