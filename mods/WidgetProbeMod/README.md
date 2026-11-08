# WidgetProbeMod（控件与对局观测探针）

在游戏左上角画一个常驻 HUD 面板，验证两件事：

1. **WidgetService 真能画**：Canvas / Panel / Text / Slider / Badge 创建、锚点布局、属性写回、作用域销毁。
2. **新版 GameEvents 真能收到**：商店报价、商店成交、筹码候选、筹码结果、刷新余量、血量差值、击杀。

事件一到，面板上立刻刷新对应分区（原始字段 + `Names` 换算的名字）。

## 面板内容

| 分区 | 数据来源 | 内容 |
|---|---|---|
| 计数 | 全部事件计数器 | 各事件收到次数，且同时订阅旧事件 `ShopCandidates` / `RelicCandidates` / `RelicSelected` 做交叉核对 |
| 商店报价 | `ShopOffered`（`ShopOffer`） | PVE/PVP、`Sn`、总价（原价/折扣）、卡名、欲购槽位、已买/免费标记、离店、转账 |
| 筹码候选 | `RelicOffered`（`RelicOffer`） | `Sn`、候选筹码名、`IsReroll`、`Lv`、`SupLv`（升星） |
| 商店成交 | `ShopSettled`（`ShopSettlement`） | `ResolveBoughtCardIds()` 换算出的卡名、槽位下标、离店、转账、`DownSn` |
| 筹码结果 | `RelicResult` | 选定/刷新、筹码名；`downSn` 在回调同步执行期间读取（离开回调后读不到） |
| 血量 / 击杀 | `HeroHpChanged` / `PlayerKilled` | 差值与累计差值、`maxHp`、击杀归属与击杀次数 |
| 拉取 | `DownSn(int)` / `RelicRerollRemaining(playerId)` | 每 0.25 秒拉一次 5030 / 5216 / 5212 的下行序号与自己剩余刷新次数，与推送事件对比 |
| 血条 | `Players.Roster()` + Slider | 自己的 HP 比例，顺带验证 `Slider.SetValue` |

右下角的 Slider 是血条；Badge 颜色表示最近事件（灰=等待，橙=商店/筹码栏，绿=成交）。

## 热键与配置

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `enabled` | `true` | 总开关 |
| `toggleKey` | `F9` | 显示/隐藏面板 |
| `clearKey` | `F8` | 清空流水与计数（分段验证用） |
| `fontSize` | `14` | 面板正文字号（12–20） |

配置在 `mods\WidgetProbeMod\config.json`，改完热重载，无需重进游戏。

## 本地验证步骤

```powershell
dotnet build mods\WidgetProbeMod\WidgetProbeMod.csproj -c Release
pwsh -File tools\deploy.ps1 -Mods WidgetProbeMod -Launch
```

进入游戏（桌面即可，不必开局）后应能看到面板，日志里出现：

```
面板构建自检 PASS: Canvas/Panel/Title/Badge/Body/Slider/Place/SetText/SetActive/SetValue 均已调用
```

然后进卡牌商店 → 商店报价与成交分区亮起；触发三选一 → 筹码候选与结果分区亮起；
受到伤害/回复 → 血量分区与血条动；击杀 → 击杀分数 +1。

看完后在游戏目录 `AstralParty_ModLoader\mods\` 删掉 `WidgetProbeMod` 文件夹即可，
不会随 CesiumLoader 内置包分发。
