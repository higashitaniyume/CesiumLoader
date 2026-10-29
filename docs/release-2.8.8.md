# CesiumLoader 2.8.8

- 新增内置模组「快捷短语扩展」（QuickChatPlusMod）：
  - 战斗内快捷短语列表补入房间等待、结算与其他模式的既有短语；PVE 列表从 16–19 条扩展到 32 条。
  - 列表超出屏幕时自动限高并支持滚轮或拖动滚动；默认同时显示 16 条，可在 `config.json` 的 `maxVisibleItems` 调整（0–60，`0` 表示按屏幕可用高度尽量多）。
  - 短语文本与发送沿用游戏原版，其他玩家无需安装本模组也能看到；模组只改变本机的列表内容。
  - 战役 PVE、练习 PVE 原本没有快捷短语列表，启用后可以正常打开。
  - 配置项：`enabled`、`includeRoomChats`、`includeResultChats`、`includePvpModes`、`maxVisibleItems`。
- 加载器 / SDK / CLI 版本统一到 2.8.8。

更新后重启游戏生效，已有模组配置与自定义值保留。
