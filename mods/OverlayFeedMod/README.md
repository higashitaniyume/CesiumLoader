# OverlayFeedMod

覆盖层数据馈送：把 `ShopRelicPeek` 同源的商店与筹码历史推给 WPF 覆盖层进程。

## 开关与配置

* `enabled` 总开关
* `showShopCards` 推送商店看板
* `showRelicCandidates` 推送筹码看板
* `shopDisplayDurationSeconds` / `relicDisplayDurationSeconds` 保留时长

## 说明

与 `ShopRelicPeekMod` 是同一套 `Action` 跟踪逻辑（`5211/5029/5215/5323/5030/5216`），本 Mod 只负责把结构化的快照经 `OverlayChannel.SendPeek` 推给 `CesiumOverlay.exe`。`ShopRelicPeekMod.backend=wpf` 已退役，请只用本 Mod 往覆盖层推送，`ShopRelicPeekMod` 留在内部（`internal`）。
