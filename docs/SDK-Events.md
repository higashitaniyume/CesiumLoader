# SDK 事件 — GameEvents

命名空间：`CesiumLoader.SDK.Gameplay`

游戏事件中枢：包装游戏 RPC 回调（S2C），把原始协议转成简单事件。所有事件在**游戏每场战斗重置回调后需重新挂钩**。

## 挂钩

```csharp
public static void GameEvents.StartAutoHook();   // 推荐: 订阅后调一次, 内部每秒自动维护
public static void GameEvents.EnsureHooked();    // 手动维护(自行 tick 时用)
```

- **必须在战斗开始后周期性调用**（推荐直接调 `StartAutoHook()` 一次，或放在 `ModBase.Run` 的 `tick` 里每秒调一次 `EnsureHooked()`）。
- 幂等：已包装的回调跳过；游戏每场战斗会重置 RPC 回调，所以每秒调用一次。
- **按需挂钩**：只有某个 RPC 喂的事件确实有订阅者时才包装它；订阅者清空后，若槽位仍是 SDK 的包装就还原成原回调。其它观测方（其它 mod / 工具）不必永远排在 SDK 后面。
- 安全：战斗相关回调只在确认战斗中挂钩（`GameLogicManager.battle` 存在且有玩家），避免启动早期强制创建 NetManager 单例崩溃。
- 匹配/选人相关回调由 `EnsureRoomHooked()` 单独挂钩：只要 `NetManager` 存在就生效，不受“必须在战斗中”的限制。`StartAutoHook()` 与 `EnsureHooked()` 都会自动调用它。
- 包装后的回调会**转发给游戏原回调**，不干扰游戏逻辑。
- 订阅方抛异常不会影响游戏与其它订阅者：SDK 对每个订阅者逐个 `try/catch`。

## 32 个事件

### 战斗用牌

| 事件 | 签名 | 触发时机 |
|---|---|---|
| `CardUsed` | `Action<long, int, int>` `(playerId, cardId, remain)` | 战斗中用牌。cardId 已从 Guid 反查为真实 CardId，remain 为剩余手牌数 |
| `NoCard` | `Action<long>` `(playerId)` | 无牌可出，跳过 |

### 效果牌 / 技能

| 事件 | 签名 | 触发时机 |
|---|---|---|
| `EffectCardUsed` | `Action<long, int, int>` `(playerId, cardId, remain)` | 棋盘效果牌 |
| `SkillUsed` | `Action<long, int>` `(playerId, skillId)` | 技能释放（效果牌 UseSkill=true 时） |
| `QuickCardUsed` | `Action<long, int, int>` `(playerId, cardId, originalCardId)` | 快速卡/跟牌（originalCardId = 被跟的卡） |

### 回合流程

| 事件 | 签名 | 触发时机 |
|---|---|---|
| `DiceResult` | `Action<long, int, int>` `(playerId, point, maxPoint)` | 掷骰子结果 |
| `Move` | `Action<long, int, bool>` `(playerId, steps, end)` | 移动（steps = 步数，end = 是否到达） |
| `RewardCardSelected` | `Action<long, int>` `(playerId, cardId)` | 回合结束选奖励卡 |

### 战斗

| 事件 | 签名 | 触发时机 |
|---|---|---|
| `BattleUpdate` | `Action<Battle>` `(b)` | 战斗更新（攻防变化/结束时触发，**已去重**）。`Battle` 为游戏类型 `party.model.Battle` |
| `BattleDice` | `Action<long, int>` `(playerId, point)` | 战斗攻击骰子 |
| `BattlePhaseChanged` | `Action<Battle, BattlePhase>` `(b, phase)` | 战斗子阶段变化。攻方投出骰子（含 0 点）时触发 |

### 战斗子阶段

服务器没有下发战斗阶段字段，`GameEvents` 按战斗快照与投骰广播的先后顺序推导：

| 成员 | 类型 | 含义 |
|---|---|---|
| `CurrentBattlePhase` | `BattlePhase` | `None` / `AttackerRollPending` / `AttackerRolled` / `Ended` |
| `AttackerHasRolled` | `bool` | 当前战斗的攻方是否已经投骰 |
| `AttackerPoint` | `int` | 攻方骰点：未投骰为 `-1`，已投骰为 `0..6` |

`BattleRole.Point` 是普通 proto3 整数：攻方尚未投骰与投出 0 点都是 `0`，仅看该字段无法判断骰子是否已经落地。判断方式用 `AttackerPoint >= 0`（或 `AttackerHasRolled`），不要用 `Point > 0`。

0 是有效点数（例如摩西【破绽】把怪物战斗骰点压到 0），按 0 点计算闪避时必定闪避成功。投出 0 点时战斗数值可能不变，`BattleUpdate` 会被去重，需要在骰子落地瞬间重算的订阅方应同时处理 `BattlePhaseChanged`。

重连后通常只拿得到战斗快照：快照里的骰点大于 0 时一律视为已投骰；骰点为 0 且没有收到投骰广播时，子阶段停在 `AttackerRollPending`。

### 商店 / 筹码 / 手牌

| 事件 | 签名 | 触发时机 |
|---|---|---|
| `ShopCandidates` | `Action<long, IReadOnlyList<int>>` `(playerId, cardIds)` | 商店待选卡（PVP 5029 / PVE 5215 action）。只给 id 列表 |
| `ShopOffered` | `Action<ShopOffer>` | 商店报价完整字段：价格、折扣、免费、已买、转账。比 `ShopCandidates` 多 |
| `ShopSettled` | `Action<ShopSettlement>` | 商店成交确认（PVE 5216 / PVP 5030）。只有这里才知道实际买了什么、是否离店 |
| `RelicCandidates` | `Action<long, IReadOnlyList<int>>` `(playerId, relicIds)` | 筹码格候选筹码（5211 action）。只给 id 列表 |
| `RelicOffered` | `Action<RelicOffer>` | 筹码候选完整字段：`Sn`、`IsReroll`、`Index`、`Lv`、`SupLv` |
| `RelicSelected` | `Action<long, int>` `(playerId, relicId)` | 筹码选择结果。**只上报真正选定**，刷新不会触发 |
| `RelicResult` | `Action<long, int, bool>` `(playerId, relicId, isReroll)` | 筹码结果完整版：`isReroll=true` 表示只是刷新 |
| `RelicRerollChanged` | `Action<long, int>` `(playerId, remain)` | 筹码剩余刷新次数变化（来自 1040 的 `Reroll` 项） |
| `RelicsSynced` | `Action<long, IReadOnlyList<int>>` `(playerId, relicIds)` | 遗物同步（开局/变更） |
| `HandChanged` | `Action<long, IReadOnlyList<CardInfo>>` `(playerId, cards)` | 手牌变化。**队友的 CardId 可能是负数**（服务器掩码） |
| `RawAction` | `Action<RawActionEvent>` `(Id, Sn, PlayerId, Data)` | **原始动作流**（1002 `PredictActionS2C` 里逐条广播，未解码）。`Data` 是原始 protobuf 字节，按 `Id` 用 `Parser.ParseFrom` 自行解码 |

> `RawAction` 是唯一带 **`Sn`（服务器操作序号）** 的事件 —— 其它事件都不带 `sn`。
> 想做"替玩家操作"这类需要回传 `sn` 的功能，必须用这条事件建窗口；
> 用不带 `sn` 的事件建窗口会得到 `Sn=0`，请求发出去服务器不认。

### 血量 / 击杀

| 事件 | 签名 | 触发时机 |
|---|---|---|
| `HeroHpChanged` | `Action<long, int, int>` `(playerId, changeHp, maxHp)` | 血量变化。**`changeHp` 是差值，可正可负，不是绝对血量** |
| `PlayerKilled` | `Action<long, long>` `(victimId, attackerId)` | 击杀消息（1077）。只有这条消息带攻击者归属 |
| `HeroAttrUpdated` | `Action<UpdateHeroAttrS2C>` `(model)` | 原始属性更新（含 `CauseOrigin`、生命变化和 `DamageType`），需要协议细节时用 |

- `HeroHpChanged` 累加即可得到累计伤害；血量归零不等于击杀，击杀归属看 `PlayerKilled`。
- 毒、灼烧、地图伤害等掉血不会有击杀消息，`PlayerKilled` 只覆盖有归属的击杀。
- 血量与击杀事件分别从 1040 `UpdateHeroAttrS2C` / 1077 `KillMessageS2C` 解出，不需要额外 RPC。

### 匹配 / 选人

| 事件 | 签名 | 触发时机 |
|---|---|---|
| `MatchSuccess` | `Action<Room>` `(room)` | 匹配成功，服务器已为本局建好房间（状态已切到选人）。在匹配成功动画之前触发 |
| `HeroSelectionStarted` | `Action<Room>` `(room)` | 选人界面打开、可以选角色时触发：匹配成功动画结束，或房主开始游戏后服务器要求选角色。要做“自动选角色”请用这条事件 |
| `HeroSelected` | `Action<long, int, bool>` `(playerId, heroId, heroTaken)` | 玩家确认角色。`heroTaken=true` 表示该角色已被其他玩家占用、服务器拒绝了这次确认 |

- `room` 是 `party.model.Room` 快照，可能为 `null`；重开一局会再次触发 `MatchSuccess` / `HeroSelectionStarted`。
- 这三条事件在战斗之外到达，由 `EnsureRoomHooked()` 挂钩，`StartAutoHook()` 已包含，无需额外轮询。
- 选择角色本身不会结束流程：`GameActions.SelectHero(heroId)` 之后还要 `GameActions.ConfirmHero()`（与游戏里“选好角色再点确定”一致）。

## 商店成交与报价

`ShopCandidates` 只给卡 id 列表，做对局统计时还需要价格与实际成交内容，因此另外提供两个富事件。

### `ShopSettled` — `ShopSettlement`

来源：PVE 5216 `PVEShopBuyS2C` / PVP 5030 `ShopBuyS2C`。

| 成员 | 类型 | 含义 |
|---|---|---|
| `PlayerId` | `long` | 成交玩家 |
| `BuyCards` | `int[]` | 本次实际买到的**候选槽位下标**（与 `Cards` 对齐），**不是卡牌 id** |
| `Cards` | `int[]` | 成交消息自带的店内卡牌配置 id |
| `Already` | `bool[]` | 成交消息自带的“已买”标记 |
| `IsClose` | `bool` | 是否离店（PVP 无此字段，恒 `false`） |
| `AssistPlayer` | `long` | PVE 转账目标（0 = 不是转账），PVP 恒 0 |
| `IsPve` | `bool` | 是否 PVE 商店 |
| `DownSn` | `long` | 这条成交消息的下行序号（读不到为 0） |
| `ResolveBoughtCardIds()` | `int[]` | 把槽位下标换算成卡牌配置 id；越界位置填 0，不抛异常 |

- `BuyCards` 是槽位下标，直接当卡 id 用会拿到错的卡。换算：`settlement.ResolveBoughtCardIds()`。
- 转账（给队友星币）时 `BuyCards` 为空数组、`AssistPlayer > 0`，不是队友代付。
- `errId != 0` 的失败回执不会触发本事件。

### `ShopOffered` — `ShopOffer`

来源：`PredictActionS2C` 里的 5215（解 `PVEShopBuyC2S`）/ 5029（解 `ShopBuyC2S`）。

| 成员 | 类型 | 含义 |
|---|---|---|
| `PlayerId` / `Sn` | `long` | 报价玩家 / 动作序号（同批动作会重复广播，按它去重） |
| `CardIds` | `int[]` | 店内卡牌配置 id |
| `BuyCards` | `int[]` | 欲购/已买的**候选槽位下标**（与 `CardIds` 对齐） |
| `AlreadyBought` | `bool[]` | “已买”标记 |
| `Free` | `bool[]` | 天赋免费标记（仅 PVE，PVP 为空数组） |
| `Gold` / `DisCountGold` | `int` | 原价 / 折扣后总价（PVP 无折扣，恒 0） |
| `FreeCard` / `FreeCardNum` | `int` | PVP 免费卡 id / 张数（PVE 恒 0） |
| `AssistPlayer` / `AssistGold` | `long` / `int` | PVE 转账目标与金额（PVP 恒 0） |
| `IsClose` / `IsPve` | `bool` | 是否离店 / 是否 PVE |
| `TotalGold` | `int` | 应付款：`DisCountGold > 0` 取折扣价，否则取原价 |
| `UnitPrices` | `int[]` | 逐张最终单价（与 `CardIds` 对齐）；未知为 `-1`，PVP 恒 `-1` |

- 协议只给**总价**，每张单价按 `(免费?特殊价:Gold) - DisCountGold` 计算（特殊价见 `ConfigReads`），天赋免费时钳制到 0。

### `RelicOffered` — `RelicOffer`

来源：`PredictActionS2C` 里的 5211（解 `SelectRelicC2S`）。

| 成员 | 类型 | 含义 |
|---|---|---|
| `PlayerId` / `Sn` | `long` | 候选玩家 / 动作序号 |
| `RelicIds` | `int[]` | 本次候选筹码 id（通常 3 个） |
| `IsReroll` | `bool` | 该批候选是否由刷新产生（与结果事件里的 `isReroll` 不是一回事） |
| `Index` / `Lv` / `SupLv` | `int` | 原始字段；`SupLv > 0` 表示这次候选是升星 |

## 筹码结果与刷新次数

- `RelicSelected(playerId, relicId)` 保持旧语义：**只在真正选定时触发**。
- `RelicResult(playerId, relicId, isReroll)` 是完整版：`isReroll=true` 表示这次只是刷新（玩家还没选），`false` 才是选定。需要区分刷新与选定就订阅这条。
- `RelicRerollChanged(playerId, remain)` 推送剩余刷新次数变化；开局时用 `GameEvents.RelicRerollRemaining(playerId)` 拉一次初始值。
- `RelicRerollRemaining` 查不到（不在战斗 / 没有该玩家 / 没有该属性）返回 **-1**，不会返回 0 —— 0 会被误判成“没次数了”。

## 查询：下行序号 downSn

结果消息（筹码结果、商店成交）本身不带“这是对哪次候选的回答”，只有游戏下行队列里的**下行序号**能把结果和候选 action 的 `Sn` 对上。

```csharp
public static long GameEvents.DownSn(int command);                  // 用 NetManager 上的 RPC
public static long GameEvents.DownSn(RPCMsgManager rpc, int command); // 指定 RPC 管理器
```

命令号常量见 `GameCommands`：`PredictAction`(1002)、`UpdateHeroAttr`(1040)、`KillMessage`(1077)、`SyncRelics`(1112)、`ShopBuy`(5030)、`SelectRelic`(5212)、`PveShopBuy`(5216)。

- **必须在对应事件的处理器里同步调用**：游戏是“把消息放在队首、未标记消费时”调用回调的，离开回调后队首就换成下一条了。异步延迟读取会拿到 0 或别的命令。
- 读不到一律返回 **0**（保守失败），不会把反射异常抛进网络回调链。
- 立即处理、不进队列的命令读不到序号：1001 / 5004 / 5008 / 5012 / 5096 / 5150 / 5238 / 5246 / 5328。
- `ShopSettlement.DownSn` 已在 SDK 内部用同样方式填好，商店成交无需自己再调。

## 结算与追击（3.5.0 新增）

### `MatchSettled` — `IReadOnlyList<PlayerFinalStats>`

来源：`GameFinishS2C.NewAchieve`。字段：`PlayerId / KillCount / TotalDamage / TotalInjured / TotalDie / TreatmentScore / MovePoint / TotalGold / PveTransferGold`（均为服务器最终值）。

- 只在对局结束推送一次；可能只含部分玩家，不要补 0。
- 结算值不覆盖本地累计，只做对账。

### `FightResolved` — `FightSnapshot`

来源：`gm.fight.battleFightData`（`isEnd && !isReConnect`），按 `BattleId` 去重。

| 成员 | 含义 |
|---|---|
| `BattleId` / `AttackerPlayerId` / `DefenderPlayerId` | 战斗与攻防归属 |
| `ChainAttackerPlayerId` | 追击方（0 = 无追击） |
| `IncHp` / `ChainAttackDamage` | 防守方增益与追击伤害 |
| `Ended` / `Reconnected` | 恒 `true` / 恒 `false`（重连不推） |

SDK 只给事实，追击归属与 HP 对账由调用方做。

### `HeroHpChangedFull` — `HeroHpChangeInfo`

与旧 `HeroHpChanged` 并行广播，字段：`PlayerId / Killer / OriHp / ChangeHp / RealChangeHp / RealHp / MaxHp`（`HeroHpChangeS2C` 全字段）。

- **`<c>RealHp == 0</c> 表示未提供, 不是血量为 0`**，不要据此判死亡。
- `ChangeHp != RealChangeHp` 时差的是溢出。

### `MovementTotalChanged` 与 `MovementTotal`

```csharp
public static event Action<long, int> MovementTotalChanged; // (playerId, 累计步数)
public static int MovementTotal(long playerId);               // 查不到/不可用返回 -1
```

- 订阅后才挂 `minimapMove`（懒挂载），累计从订阅时刻起算。
- 按到达确认记步：`Move` 的节点数不是步数，不要累加它。
- 换局清空，重连（`RoomId/StartTime` 不变）不清空。

## 用法示例

```csharp
static void OnInit()
{
    GameEvents.CardUsed += (pid, cardId, remain) =>
        SdkLog.Info("MyMod", $"玩家{pid} 使用 {Names.Card(cardId)} 剩{remain}张");
    GameEvents.BattleUpdate += b =>
        SdkLog.Info("MyMod", $"战斗 {Names.BattleRole(b.Attacker)} vs {Names.BattleRole(b.Defender)} 攻{b.Attacker.Atk} 防{b.Defender.Def}");
    GameEvents.MatchSuccess += room =>
        SdkLog.Info("MyMod", "匹配成功" + (room != null ? "，房间 " + room.Id : ""));
    GameEvents.HeroSelectionStarted += room =>
        SdkLog.Info("MyMod", "进入选人阶段");

    GameEvents.ShopSettled += s =>
        SdkLog.Info("MyMod", $"买了 {string.Join(",", s.ResolveBoughtCardIds())}，downSn={s.DownSn}");
    GameEvents.RelicResult += (pid, relicId, isReroll) =>
        SdkLog.Info("MyMod", isReroll ? $"玩家{pid} 刷新筹码" : $"玩家{pid} 选定 {Names.Relic(relicId)}");
    GameEvents.HeroHpChanged += (pid, delta, max) =>
        SdkLog.Info("MyMod", $"玩家{pid} 血量 {delta:+#;-#;0} (上限{max})");
    GameEvents.PlayerKilled += (victim, attacker) =>
        SdkLog.Info("MyMod", $"玩家{victim} 被 玩家{attacker} 击杀");

    GameEvents.StartAutoHook();   // 订阅完调一次, 内部每秒维护挂钩
}

static void OnTick()
{
    // 也可以不用 StartAutoHook, 自己每秒调一次:
    // GameEvents.EnsureHooked();
}
```

## 注意事项

- 事件参数里的 `playerId` 都是服务器下发的 `long`（玩家 ID），可用 `Players.IsSelf(pid)` 判断是否是自己。
- `HandChanged` 队友手牌 CardId 为负数（服务器掩码），`Names.Card` 对负数会返回 `卡{id}`。
- `BattleUpdate` 已按"战斗子阶段 + 攻防数值 + 结束状态"去重，相同状态不会重复触发。
- 攻方投出 0 点时数值不变，但 `BattlePhaseChanged` 仍会触发；需要“骰子落地就重算”的 mod 两个事件都订阅。
- `ShopCandidates` / `RelicCandidates` / `ShopOffered` / `RelicOffered` 内部按 action Sn 去重，同一候选只报一次。
- 同名事件有新旧两套时，旧事件语义不变：`RelicSelected` 只报选定，`ShopCandidates`/`RelicCandidates` 只给 id 列表。需要完整字段就订阅对应的富事件。
- `BuyCards` 是候选槽位下标，不是卡牌 id；商店成交用 `ShopSettlement.ResolveBoughtCardIds()` 换算。
- 按需挂钩意味着：没有订阅者的 RPC 不会被包装，订阅者全部退订后会还原原回调；重新订阅后下一次 `EnsureHooked()` 会重新安装。
- 事件回调可能来自网络线程，`RawAction` 尤其如此；订阅方自己保证线程安全，不要在回调里直接碰 Unity API。
