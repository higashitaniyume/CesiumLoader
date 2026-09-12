# SDK 操作 — GameActions

命名空间：`CesiumLoader.SDK`

> ⚠️ **警告**：本类的方法会**真实影响对局** —— 让 mod 像玩家一样向服务器发送 C2S 指令（投骰子/移动/用牌等）。只在你的 mod 确实需要时调用，滥用可能导致对局异常或封号风险。

内部复刻游戏 UI 的真实发送路径（`NetManager.RPC.xxxCall`），与玩家手动操作走**同一条链路**，服务器按正常逻辑处理。

## 通用行为

- 所有方法带空保护：不在战斗/房间时**静默失败返回 `false`**，绝不抛异常。
- 所有方法返回 `bool`：是否成功发出（不代表服务器接受）。
- 所有方法可选 `long? sn = null` 参数：默认取服务器下发的当前操作序列号（`action.throwDiceSn`），一般无需手动指定。

## 属性

| 成员 | 类型 | 说明 |
|---|---|---|
| `CurrentSn` | `long` (get) | 当前待响应的操作序列号（最近一个 Action 的 Sn）。不在回合/无操作时为 0 |
| `CanThrowDice` | `bool` (get) | 是否能投骰子（轮到自己的行动回合，`throwDiceSn > 0`） |

## 投骰子

```csharp
public static bool ThrowDice(bool isNoOper = false, bool isMoveNow = false, long? sn = null);
public static bool BattleThrowDice(long? sn = null);
```

| 参数 | 说明 |
|---|---|
| `isNoOper` | 无牌可出直接跳过 |
| `isMoveNow` | 投完立即移动 |
| `BattleThrowDice` | 战斗中的攻击判定骰 |

## 移动

```csharp
public static bool Move(int targetLandId, long? sn = null);
```

`targetLandId` = 目标地块 ID（方向箭头指向的格）。

## 用牌

```csharp
public static bool UseCard(int cardId, long? sn = null);
public static bool UseEffectCard(int cardId, IEnumerable<long> targetIds = null,
    IEnumerable<int> landIds = null, int chooseEffectIndex = 0, long? sn = null);
public static bool UseQuickCard(int cardId, long targetId, long? sn = null);
public static bool AbandonCards(IEnumerable<int> cardIds, long? sn = null);
public static bool AbandonCard(int cardId, long? sn = null);
```

| 方法 | 说明 |
|---|---|
| `UseCard` | 战斗中使用卡牌（`cardId` = 手牌里卡牌的 CardId） |
| `UseEffectCard` | 使用棋盘效果牌。`targetIds` = 目标玩家，`landIds` = 目标地块，`chooseEffectIndex` = 选择的效果项 |
| `UseQuickCard` | 跟牌/快速卡。`cardId` = 快速卡 CardId，`targetId` = 被跟的玩家 id |
| `AbandonCards` / `AbandonCard` | 弃牌（CardId 列表 / 单张） |

## 选择 / 结算

```csharp
public static bool SelectRewardCard(IEnumerable<int> cardIds, int selectedIndex, long? sn = null);
public static bool SelectRelic(IEnumerable<int> relicIds, int selectedIndex = 0, long? sn = null);
public static bool SelectRelic(int relicId, long? sn = null);   // 便捷重载
```

| 方法 | 说明 |
|---|---|
| `SelectRewardCard` | 选择奖励卡。`cardIds` = 服务器下发的候选列表（可用 `GameEvents.RewardCardSelected`/`ShopCandidates` 拿到），`selectedIndex` = 选中的下标 |
| `SelectRelic` | 选择遗物（筹码格）。`relicIds` = 服务器下发的候选（`GameEvents.RelicCandidates`），`selectedIndex` = 选中的下标 |

## 完整示例：自动投骰 + 自动出牌

```csharp
static void OnTick()
{
    GameEvents.EnsureHooked();

    // 轮到我就投骰子
    if (GameActions.CanThrowDice)
    {
        if (GameActions.ThrowDice())
            SdkLog.Info("Auto", "自动投骰");
    }
}
```

```csharp
// 自动跟牌: 别人出牌我也出同一张 (快速卡)
GameEvents.QuickCardUsed += (pid, cardId, originalCardId) =>
{
    if (!Players.IsSelf(pid))
        GameActions.UseQuickCard(cardId, pid);
};
```

## 注意事项

- **不要每个 tick 都发操作** —— 服务器有 Sn 校验，无效操作会被拒绝且可能干扰对局。用事件驱动（订阅 `GameEvents.*`）而不是轮询触发。
- `cardId` 语义：`GameActions.UseCard` 用的是**手牌 CardId**（即 `Players.MyHandCards()` 里的 `CardId`，或事件里 `ResolveCardGuid` 反查后的值），不是配置表 id。
- 所有操作在发送前调用 `logic.battle.RecordFinishSn(sn)` 记录，与游戏 UI 行为一致。
