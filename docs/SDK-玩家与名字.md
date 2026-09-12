# SDK 玩家与名字

命名空间：`CesiumLoader.SDK`

## Players — 玩家数据访问

所有方法都做空保护：不在战斗/房间时返回空结果，**绝不抛异常**。

```csharp
public static class Players
{
    // 列表与查询
    public static IReadOnlyList<BattlePlayerData> All();      // 当前战斗玩家列表 (空 = 不在战斗)
    public static BattlePlayerData Get(long playerId);        // 按 id 查, 查不到 null
    public static bool IsSelf(long playerId);                 // 是否是"我"

    // 快捷数据
    public static int Gold(long playerId);                    // 星币, 查不到 0
    public static int HandCount(long playerId);               // 手牌数, 查不到 0
    public static string Nick(long playerId);                 // 昵称(只查玩家列表), 查不到 null
    public static string SafeNick(BattlePlayerData pd);       // 安全昵称: 空/纯空白返回 null

    // 手牌
    public static IReadOnlyList<HandCardData> MyHandCards();  // 自己的手牌 (队友被服务器掩码)
    public static int ResolveCardGuid(long playerId, int cardGuid); // cardGuid 反查真实 CardId
}
```

### 类型说明

- `BattlePlayerData`、`HandCardData` 来自游戏热更程序集（`party.model`）。
- `BattlePlayerData` 常用字段：`Property.gold.Value`（星币）、`cardContainer.CardCount`（手牌数）、`cardContainer._HandCards`（手牌）。
- `ResolveCardGuid`：战斗用牌事件里的 `CardId` 实际是手牌 Guid，此方法从该玩家手牌容器反查真实 CardId；查不到原样返回。

### 用法示例

```csharp
// 列出所有玩家星币
foreach (var p in Players.All())
{
    var nick = Players.SafeNick(p);
    if (nick == null) continue;  // 跳过幽灵条目
    SdkLog.Info("MyMod", $"{nick} 星币 {Players.Gold(p.player.PlayerId)}");
}

// 判断事件是否与自己有关
GameEvents.CardUsed += (pid, cardId, remain) =>
{
    if (Players.IsSelf(pid))
        SdkLog.Info("MyMod", $"我出牌: {Names.Card(cardId)}");
};
```

## Names — 名字解析

全部带缓存 + 查不到时的明确 fallback。

```csharp
public static class Names
{
    public static string Card(int cardId);       // 卡牌名: "真实名(id)", 查不到"未知卡{id}"
    public static string Relic(int relicId);     // 遗物名: "真实名(id)", 查不到"遗物{id}"
    public static string Skill(int skillId);     // 技能名: "真实名(id)", 查不到"技能{id}"
    public static string Character(int characterId); // 角色/怪物名, 查不到"角色{id}"
    public static string BattleRole(BattleRole role); // 战斗角色名
}
```

### 解析规则

| 方法 | 查找来源 | Fallback |
|---|---|---|
| `Card` | 卡牌配置表 → 技能表（卡表查不到可能是技能卡） | `未知卡{id}`（id ≤ 0 时 `卡{id}`） |
| `Relic` | 遗物配置表 | `遗物{id}` |
| `Skill` | 技能配置表 | `技能{id}` |
| `Character` | `CharacterHandle.GetCharacterName` | `角色{id}` |
| `BattleRole` | 玩家昵称 → 怪物名（HeroId + CharacterType.Monster） | `P{PlayerId}` |

### 用法示例

```csharp
GameEvents.ShopCandidates += (pid, cardIds) =>
{
    var names = string.Join(" | ", cardIds.Select(Names.Card));
    SdkLog.Info("MyMod", $"玩家{pid} 商店待选: {names}");
};

GameEvents.BattleUpdate += b =>
    SdkLog.Info("MyMod", $"{Names.BattleRole(b.Attacker)} vs {Names.BattleRole(b.Defender)}");
```
