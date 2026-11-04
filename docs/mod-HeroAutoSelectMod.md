# 自动选角色（HeroAutoSelectMod，示例）

匹配成功或进入选人阶段后，自动选择配置里指定的角色并确认。默认目标为 **102 芬妮「古怪神探」**。

本模组是 SDK 选人事件与选人操作 API 的示例，不随加载器内置清单分发；需要按下方步骤自行构建并放入 mods 目录。

## 行为

1. 匹配成功时记录房间；匹配流程结束、选人界面打开后开始计时。
2. 等待 `DelayMilliseconds`（默认 500 毫秒），确认处于选人阶段后调用 `GameActions.SelectHero(HeroId)`。
3. `AutoConfirm` 为 `true` 时，再等待 `ConfirmDelayMilliseconds`（默认 500 毫秒）调用 `GameActions.ConfirmHero()`。
4. 目标角色已被其他玩家占用时，服务器会拒绝确认，日志提示并停止，不会改选其他角色。
5. 不在选人阶段时会每 0.5 秒重试一次，最多约 6 秒；超时后放弃并记录警告。

匹配成功、进入选人阶段、角色确认结果都会写入加载器日志。

## 配置

配置位于 `AstralParty_ModLoader/mods/HeroAutoSelectMod/config.json`，可在 AstralParty.Toys 的模组配置页面编辑。

| 键 | 默认值 | 说明 |
|---|---|---|
| `Enabled` | `true` | 总开关 |
| `HeroId` | `102` | 要自动选择的角色 ID。`102` = 芬妮「古怪神探」 |
| `AutoConfirm` | `true` | 选择后是否自动点「确定」 |
| `DelayMilliseconds` | `500` | 进入选人阶段后等待多久再选择（0–10000） |
| `ConfirmDelayMilliseconds` | `500` | 发出选择后等待多久再确认（0–10000） |

## 安装

在工作区根目录构建，然后把 DLL 与 sidecar 放进模组目录：

```powershell
dotnet build modding/msvc/mods/HeroAutoSelectMod/HeroAutoSelectMod.csproj -c Release --nologo
```

将 `mods/HeroAutoSelectMod/bin/Release/netstandard2.0/HeroAutoSelectMod.dll` 与
`mods/HeroAutoSelectMod/HeroAutoSelectMod.json` 放入
`AstralParty_ModLoader/mods/HeroAutoSelectMod/`，重启游戏。首次启动会生成带注释的 `config.json`。

本模组需要 `GameActions` 权限（已在 sidecar 中声明），并依赖 SDK 3.2.0 及以上。

## 相关 API

- `GameEvents.MatchSuccess`、`GameEvents.HeroSelectionStarted`、`GameEvents.HeroSelected`
- `GameActions.CanSelectHero`、`GameActions.SelectHero(int heroId)`、`GameActions.ConfirmHero(bool auto)`

详见 [SDK-事件.md](SDK-事件.md) 与 [SDK-操作.md](SDK-操作.md)。
