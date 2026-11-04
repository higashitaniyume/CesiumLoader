# CesiumLoader 3.2.0

## SDK：匹配与选人 API

- 新增事件 `GameEvents.MatchSuccess`、`HeroSelectionStarted`、`HeroSelected`，覆盖匹配成功、进入选人界面与角色确认结果。
- 新增 `GameEvents.EnsureRoomHooked()`：匹配/选人消息在战斗之外到达，由 SDK 单独挂钩，`StartAutoHook()` 已包含。
- 新增操作 `GameActions.CanSelectHero`、`GameActions.SelectHero(int heroId)`、`GameActions.ConfirmHero(bool auto = false)`。
- `TurnFocusMod` 改用 SDK 的统一匹配成功事件，不再自行包装对应 RPC。
- 新增源码示例 `HeroAutoSelectMod`：进入选人阶段后自动选择指定角色并确认，默认角色 102。该示例不随加载器内置包分发。

## 战斗胜率助手（CombatOddsMod）

- **手牌概率预览**：出牌阶段在手牌上显示每张可用战斗牌的概率。攻击牌显示打出后的击杀率，防御牌显示打出后的存活率，两者都以防守方选择防御为条件，出牌结果同步后自动重算。
- **固定点数牌**：支持暗影突袭、蓄力、全力攻击与龙之咆哮等固定点数牌；全力攻击的最终攻击倍率最多生效一次，龙之咆哮会降低目标基础防御。
- **显示调整**：新增 `HandOddsFontSize`、`HandOddsOffsetX`、`HandOddsOffsetY`，分别调整手牌概率的字号与横纵位置。

---

加载器 / SDK / CLI 版本统一到 3.2.0。更新后重启游戏生效，已有模组配置与自定义值保留。
