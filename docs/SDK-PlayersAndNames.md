# SDK 玩家与名字

命名空间：`CesiumLoader.SDK.Gameplay`

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
    public static long Score(long playerId);                  // 模式分数(SpecialScore), 查不到 0
    public static int HandCount(long playerId);               // 手牌数, 查不到 0
    public static string Nick(long playerId);                 // 昵称(只查玩家列表), 查不到 null
    public static string SafeNick(BattlePlayerData pd);       // 安全昵称: 空/纯空白返回 null
    public static bool TryGetMatch(out MatchInfo info);       // 当前有效对局上下文, 大厅/未就绪返回 false

    // 手牌
    public static IReadOnlyList<HandCardData> MyHandCards();  // 自己的手牌 (队友被服务器掩码)
    public static int ResolveCardGuid(long playerId, int cardGuid); // cardGuid 反查真实 CardId
}
```

### 类型说明

- `BattlePlayerData`、`HandCardData` 来自游戏热更程序集（`party.model`）。
- `BattlePlayerData` 常用字段：`Property.gold.Value`（星币）、`Property.Score.Value`（模式分数）、`cardContainer.CardCount`（手牌数）、`cardContainer._HandCards`（手牌）。
- `ResolveCardGuid`：战斗用牌事件里的 `CardId` 实际是手牌 Guid，此方法从该玩家手牌容器反查真实 CardId；查不到原样返回。
- `PlayerStat`（`Roster()` 返回）新增 `Slot`（座位号，0 起，异常时按名册顺序兜底）与 `Character`（角色名，缺失为空串）。关闭自动排序时按 `Slot` 稳定排列。
- `MatchInfo`：`RoomId` / `StartTime` / `MapType` / `IsPve` / `InBattle`。`RoomId + StartTime` 同时不变视为同一局（重连不换局），换局至少一项变化；不在有效对局时 `TryGetMatch` 返回 `false`。

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
    public static string Card(int cardId);                     // 卡牌名: "真实名(id)", 查不到"未知卡{id}"
    public static string Card(int cardId, bool withId);        // withId=false 只返回名字
    public static string Relic(int relicId);                   // 遗物名: "真实名(id)", 查不到"遗物{id}"
    public static string Relic(int relicId, bool withId);      // withId=false 只返回名字
    public static string Skill(int skillId);                   // 技能名: "真实名(id)", 查不到"技能{id}"
    public static string Skill(int skillId, bool withId);      // withId=false 只返回名字
    public static string Character(int characterId); // 角色/怪物名, 查不到"角色{id}"
    public static string BattleRole(BattleRole role); // 战斗角色名
}
```

### 解析规则

| 方法 | 查找来源 | Fallback |
|---|---|---|
| `Card` | 卡牌配置表 → 技能表（卡表查不到可能是技能卡） | `未知卡{id}`（id ≤ 0 时 `卡{id}`）；`withId=false` 未知时为 `未知卡牌` |
| `Relic` | 遗物配置表 | `遗物{id}`；`withId=false` 未知时为 `未知筹码` |
| `Skill` | 技能配置表 | `技能{id}`；`withId=false` 未知时为 `未知技能` |
| `Character` | `CharacterHandle.GetCharacterName` | `角色{id}` |
| `BattleRole` | 玩家昵称 → 怪物名（HeroId + CharacterType.Monster） | `P{PlayerId}` |

原有单参签名与输出保持不变；`withId` 用新增重载实现（不使用可选参数，否则已编译的旧 mod 会报 `MissingMethodException`）。

## ConfigReads — 配置表读取

```csharp
public static class ConfigReads
{
    public static int SkillParam(int skillId, int index);  // 负下标从末位倒数; 越界/缺失返回 -1
    public static int RelicQuality(int relicId);           // 0=无 1=蓝 2=紫 3=橙; 缺失返回 0
}
```

- 天赋免费特殊价取 `TalentFreeSkillId`（`10112`）的末位参数：`ConfigReads.SkillParam(ConfigReads.TalentFreeSkillId, -1)`。

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
