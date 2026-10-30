# CesiumLoader 2.8.10

- 新增内置模组「帧率解锁」（UnlockFrameRateMod）：
  - 在游戏「设置 → 屏幕 → 帧率」下拉框里追加 **144 / 165 / 240 / 360 / 不限帧** 五个选项，选中后由游戏自己应用并保存。
  - 原生 30/60/90/120 选项不变；游戏重建配置表后会自动补回追加项。
  - 「不限帧」需要关闭游戏内垂直同步（勾选垂直同步时游戏会置灰帧率下拉框）。
  - 关闭模组即恢复原版列表。
- SDK 新增 `CesiumLoader.SDK.Engine.UnityFrameRate`：`Target`、`VSync`、`SetTarget`、`SetUnlimited`、`SetVSync`、`Describe`，用于在运行时读写目标帧率与垂直同步。
- 加载器 / SDK / CLI 版本统一到 2.8.10。

更新后重启游戏生效，已有模组配置与自定义值保留。
