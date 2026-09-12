# SDK 事件 — GameEvents

命名空间：`CesiumLoader.SDK`

游戏事件中枢：包装游戏 RPC 回调（S2C），把原始协议转成简单事件。所有事件在**游戏每场战斗重置回调后需重新挂钩**。

## 挂钩

```csharp
public static void GameEvents.EnsureHooked();
```

- **必须在战斗开始后周期性调用**（推荐放在 `ModBase.Run` 的 `tick` 里，每秒一次）。
- 幂等：已包装的回调跳过；游戏每场战斗会重置 RPC 回调，所以每秒调用一次。
- 安全：只在确认战斗中挂钩（`GameLogicManager.battle` 存在且有玩家），避免启动早期强制创建 NetManager 单例崩溃。
- 包装后的回调会**转发给游戏原回调**，不干扰游戏逻辑。

## 15 个事件

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

### 商店 / 遗物 / 手牌

| 事件 | 签名 | 触发时机 |
|---|---|---|
| `ShopCandidates` | `Action<long, IReadOnlyList<int>>` `(playerId, cardIds)` | 商店待选卡（PVP 5029 / PVE 5215 action） |
| `RelicCandidates` | `Action<long, IReadOnlyList<int>>` `(playerId, relicIds)` | 筹码格候选遗物（5211 action） |
| `RelicSelected` | `Action<long, int>` `(playerId, relicId)` | 筹码选择结果 |
| `RelicsSynced` | `Action<long, IReadOnlyList<int>>` `(playerId, relicIds)` | 遗物同步（开局/变更） |
| `HandChanged` | `Action<long, IReadOnlyList<CardInfo>>` `(playerId, cards)` | 手牌变化。**队友的 CardId 可能是负数**（服务器掩码） |

## 用法示例

```csharp
static void OnInit()
{
    GameEvents.CardUsed += (pid, cardId, remain) =>
        SdkLog.Info("MyMod", $"玩家{pid} 使用 {Names.Card(cardId)} 剩{remain}张");
    GameEvents.BattleUpdate += b =>
        SdkLog.Info("MyMod", $"战斗 {Names.BattleRole(b.Attacker)} vs {Names.BattleRole(b.Defender)} 攻{b.Attacker.Atk} 防{b.Defender.Def}");
}

static void OnTick()
{
    GameEvents.EnsureHooked();  // 关键: 每秒保持挂钩
}
```

## 注意事项

- 事件参数里的 `playerId` 都是服务器下发的 `long`（玩家 ID），可用 `Players.IsSelf(pid)` 判断是否是自己。
- `HandChanged` 队友手牌 CardId 为负数（服务器掩码），`Names.Card` 对负数会返回 `卡{id}`。
- `BattleUpdate` 已按"攻防数值或结束状态变化"去重，相同状态不会重复触发。
- `ShopCandidates` / `RelicCandidates` 内部按 action Sn 去重，同一候选只报一次。
