# 快捷短语扩展 QuickChatPlusMod 1.0.0

战斗内快捷短语列表补入游戏 Chat 配置里的全部既有短语：房间等待、结算与其他模式的条目都会显示在 PVE 列表中。当前版本 PVE 列表通常为 16–19 条，扩展后每个 PVE 模式都是 32 条。

短语文本与发送都沿用游戏原版，其他玩家不需要安装本模组也能看到同样的内容。

## 安装

需要 CesiumLoader SDK 2.8.6 或更高版本。把 QuickChatPlusMod 文件夹解压到游戏 `AstralParty_ModLoader/mods/` 下，重启游戏。

## 配置

修改 `config.json`，保存后约 1 秒热重载：

- `enabled`：总开关，默认 `true`。
- `includeRoomChats`：加入房间等待短语，默认 `true`。
- `includeResultChats`：加入结算短语，默认 `true`。
- `includePvpModes`：也给 PVP 等非 PVE 模式补全，默认 `false`。
- `maxVisibleItems`：列表最多同时显示几条，超出的部分用滚轮或拖动滚动；默认 `16`，`0` 表示按屏幕可用高度尽量多，范围 `0`–`60`。

## 说明

- 覆盖 PVE、词条 PVE、战役 PVE、练习 PVE；战役与练习 PVE 原版列表为空，启用后可正常打开。
- 列表超出屏幕时会自动限高并开启滚动，贴近底部时整体上移。
- 地图限定短语会放开到所有地图，关闭后恢复。
- 只改本机列表，不发送消息；自定义文本无法显示在未安装本模组的玩家客户端。
- 观战与新手房没有聊天按钮，不生效。

许可：AGPL-3.0。
