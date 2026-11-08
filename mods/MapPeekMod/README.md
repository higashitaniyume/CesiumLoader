# 选人地图预览（MapPeekMod）

在选择角色阶段（`选择角色 / 选择皮肤 / 准备`）于左上角显示本局地图，选角前即可确认要玩的地图；进入 `游戏中` 后自动隐藏。纯只读，不修改选人流程与游戏状态。

## 功能

- **选人阶段显示地图**：显示地图名、地图 ID、玩法模式（PVE / PVP）、难度与对局人数。
- **阶段后自动隐藏**：仅在选人/选皮肤/准备阶段显示，开始游戏后立即隐藏，不遮挡战斗画面。
- **可选字段**：地图 ID、模式、难度、人数可分别开关；浮窗位置与字号可调。

## 配置项

配置文件位于 `AstralParty_ModLoader/mods/MapPeekMod/config.json`，可在 Toys 玩具箱设置中直接调整：

| 键名 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `enabled` | boolean | `true` | 总开关：选人阶段是否显示本局地图 |
| `fontSize` | int | `18` | 浮窗文本字号（范围 12–36） |
| `panelWidth` | float | `340.0` | 浮窗宽度（像素，范围 200–600） |
| `offsetX` | float | `20.0` | 距离屏幕左边缘的水平偏移量（像素） |
| `offsetY` | float | `70.0` | 距离屏幕顶部的垂直偏移量（像素） |
| `showMapId` | boolean | `true` | 是否显示地图 ID |
| `showMode` | boolean | `true` | 是否显示玩法模式 |
| `showDifficulty` | boolean | `true` | 是否显示难度 |
| `showPlayerCount` | boolean | `true` | 是否显示对局人数 |
