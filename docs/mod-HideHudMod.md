# 隐藏 HUD HideHudMod

战斗界面的一键清屏工具。默认按住反引号键 `` ` ``（Tab 上方）隐藏 HUD，松开恢复；也可以改成按一下切换。

只改游戏界面的可见性，不拦截鼠标键盘，不向服务器发送操作，也不修改对局数据。

## 操作

| 操作 | 效果 |
| --- | --- |
| 按住 `` ` ``（默认 0.35 秒） | 隐藏 HUD；松开立即恢复 |
| 短按 `` ` `` | 不触发（长按模式） |
| 按一下 `` ` ``（`pressMode=Toggle`） | 隐藏；再按一下恢复 |

默认键是 Unity 键名 `BackQuote`，也就是 Tab 上方的反引号键。配置里也可以直接写 `` ` `` 或 `~`。

## 隐藏范围

| 配置项 | 默认 | 内容 |
| --- | --- | --- |
| `hideAllHud` | `false` | 强制隐藏全部战斗 HUD，开启时忽略下面四项。作为“一键全隐藏”的总开关 |
| `hideHandCards` | `true` | 底部手牌区，包括手牌、技能按钮和出牌提示 |
| `hideMonsterDisplay` | `true` | 怪物头顶的名字、编号、血量、攻防、移动力和 Buff 信息条 |
| `hideAvatars` | `true` | 左侧玩家头像列表（头像、昵称、等级、血量、星币、名次等） |
| `hideOther` | `true` | 其余战斗 HUD：小地图、回合/胜利条件、攻击/防御、Buff 列表、角色立绘、PVE 进度、玩家状态图标、操作倒计时、战斗提示等 |

默认四项分类全部开启，所以按键就是隐藏整套战斗 HUD。想只隐藏其中一部分时，把不需要隐藏的分类改成 `false`。

`hideAllHud=true` 会直接隐藏整套 HUD；它优先于四个分类开关。若希望逐项配置，保持它为 `false`。

隐藏头像或手牌时，其他 mod 挂在这些容器里的内容（例如头像旁的手牌数、技能 CD）也会随父容器一起隐藏。

## 配置

配置文件：`AstralParty_ModLoader\mods\HideHudMod\config.json`。首次运行会自动生成；保存后约 1 秒内生效。改键或改模式会先恢复 HUD，再按新设置工作。

```json
{
  "enabled": true,
  "toggleKey": "BackQuote",
  "pressMode": "Hold",
  "holdSeconds": 0.35,
  "hideAllHud": false,
  "hideHandCards": true,
  "hideMonsterDisplay": true,
  "hideAvatars": true,
  "hideOther": true,
  "notify": false
}
```

| 字段 | 说明 |
| --- | --- |
| `enabled` | 总开关；`false` 时恢复 HUD 并停止响应热键 |
| `toggleKey` | 热键，默认 `BackQuote`。使用 Unity `KeyCode` 名称，如 `F8`、`Tab`、`Insert`；也接受 `` ` ``、`~`、`Esc` |
| `pressMode` | `Hold` = 长按隐藏、松开恢复；`Toggle` = 按一下切换 |
| `holdSeconds` | 长按触发时间，默认 `0.35` 秒，限制在 0.1–2 秒 |
| `hideAllHud` | 强制隐藏全部 HUD，忽略下面四项 |
| `hideHandCards` | 隐藏手牌区 |
| `hideMonsterDisplay` | 隐藏怪物信息条 |
| `hideAvatars` | 隐藏左侧玩家头像列表 |
| `hideOther` | 隐藏其余战斗 HUD |
| `notify` | 隐藏/恢复时弹一条屏幕提示；默认关闭，避免清屏时还出现提示 |

`toggleKey` 和 `pressMode` 也可以在 Toys 的「模组 → 隐藏HUD → 配置」中修改。

## 说明

- 只处理战斗 HUD。设置、结算、选牌/选怪等交互窗口不在隐藏范围内，避免按键后无法继续操作。
- 隐藏期间游戏仍照常接收操作；再按一次或松开热键即可恢复。
- 与手牌查看、战斗胜率助手等界面类 mod 共存；它们依附在被隐藏容器上的元素会一并消失，恢复后重新出现。
- `HideHudMod` 未启用时不会改动任何界面。
