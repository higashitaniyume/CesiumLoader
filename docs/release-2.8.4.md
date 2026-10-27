# CesiumLoader 2.8.4

- 新增内置模组「隐藏 HUD」：按住反引号键（Tab 上方）隐藏战斗 HUD，松开恢复；也可以改成按一下切换。
  - 默认键 `BackQuote`，长按 0.35 秒触发；`pressMode=Toggle` 改为切换。
  - 手牌、怪物信息、头像与其他 HUD 可分别开关（`hideHandCards`、`hideMonsterDisplay`、`hideAvatars`、`hideOther`），`hideAllHud` 可一键全隐藏。
  - 只改界面可见性，不拦截鼠标键盘，不向服务器发送操作。
- 战斗胜率助手 2.5.0：怪物名字/血量信息条右侧显示主动技能冷却，例如 `CD 2/3`，白字黑描边、字号小于名字。
  - 释放主动技能时取服务器下发的本次冷却，之后每轮 -1；默认只显示 BOSS 与配表里真正带主动技能的怪物，普通/精英的攻击类技能不显示（可用 `ShowMonsterCdForAllMonsters` 打开全部）。
  - 位置可用 `MonsterCdOffsetX` / `MonsterCdOffsetY` 微调。
- 加载器 / SDK / CLI 版本统一到 2.8.4。

更新后重启游戏生效，已有模组配置保留。
