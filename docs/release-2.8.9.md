# CesiumLoader 2.8.9

- 战斗胜率助手（CombatOddsMod）：
  - 攻方投出 0 点时不再被当作“尚未投骰”。摩西【破绽】等把怪物战斗骰点压到 0 时，战斗面板按有效 0 点计算，正确给出闪避必定成功。
  - 攻方尚未投骰时仍按最坏情况（`MaxAtk` 加判定骰）估算，不会把未投出的 0 当成已投出的点数。
- SDK 新增战斗子阶段：
  - `GameEvents.CurrentBattlePhase`、`GameEvents.AttackerHasRolled`、`GameEvents.AttackerPoint` 区分“骰子尚未落地”与“投出 0 点”；`AttackerPoint` 未投骰时为 `-1`。
  - 新增 `GameEvents.BattlePhaseChanged` 事件。攻方投出 0 点时战斗数值不变，`BattleUpdate` 会被去重，订阅该事件才能在骰子落地时立即刷新。
- 加载器 / SDK / CLI 版本统一到 2.8.9。

更新后重启游戏生效，已有模组配置与自定义值保留。
