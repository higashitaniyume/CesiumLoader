using System;
using System.Collections.Generic;
using GameLogic;
using party.model;
using Tools;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 玩家数据访问。所有方法都做空保护, 不在战斗/房间时返回空结果, 绝不抛异常。
    /// </summary>
    public static class Players
    {
        /// <summary>当前战斗中的玩家列表(空 = 不在战斗)。</summary>
        public static IReadOnlyList<BattlePlayerData> All()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm?.battle?.PlayerDatas != null)
                    return gm.battle.PlayerDatas;
            }
            catch { }
            return Array.Empty<BattlePlayerData>();
        }

        /// <summary>按 playerId 查玩家数据, 查不到返回 null。</summary>
        public static BattlePlayerData Get(long playerId)
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm?.battle != null && gm.battle.PlayerDatas != null)
                    return gm.battle.GetPlayerDataById(playerId);
            }
            catch { }
            return null;
        }

        /// <summary>玩家星币数, 查不到返回 0。</summary>
        public static int Gold(long playerId)
        {
            try
            {
                var pd = Get(playerId);
                return pd?.Property?.gold?.Value ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>玩家手牌数, 查不到返回 0。</summary>
        public static int HandCount(long playerId)
        {
            try
            {
                var pd = Get(playerId);
                return pd?.cardContainer?.CardCount ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>玩家昵称(只查玩家列表), 查不到返回 null。</summary>
        public static string Nick(long playerId)
        {
            try
            {
                var pd = Get(playerId);
                return SafeNick(pd);
            }
            catch { return null; }
        }

        /// <summary>安全昵称: 空/纯空白昵称返回 null(跳过幽灵条目)。</summary>
        public static string SafeNick(BattlePlayerData pd)
        {
            try
            {
                if (pd?.player == null) return null;
                string n = pd.player.GetNick();
                if (string.IsNullOrWhiteSpace(n)) return null;
                return n;
            }
            catch { return null; }
        }

        /// <summary>是否是"我"。</summary>
        public static bool IsSelf(long playerId)
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                return gm?.account?.IsSelf(playerId) ?? false;
            }
            catch { return false; }
        }

        /// <summary>自己的手牌内容(队友手牌被服务器掩码, 拿不到)。</summary>
        public static IReadOnlyList<HandCardData> MyHandCards()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var room = gm?.room?.curRoomInfo;
                var me = room?.GetSelfInfo();
                if (me?.cardContainer?._HandCards != null)
                    return me.cardContainer._HandCards;
            }
            catch { }
            return Array.Empty<HandCardData>();
        }

        /// <summary>一名角色的即时攻防面板数据(全部做过空保护)。</summary>
        public struct PlayerStat
        {
            public long Id;
            public string Name;
            public int Atk;
            public int Def;
            public int Hp;
            public int MaxHp;
            public bool IsSelf;
            public bool IsMonster;
            public bool IsBot;
        }

        /// <summary>
        /// 当前房间全体角色的攻/防/血面板(先玩家、后怪物)。不在房间时返回空。
        /// 数据取自 <c>RoomInfo.Players/Monsters</c> 的 <c>BattleProperty</c>(ATK/DEF/HP/maxHP)。
        /// </summary>
        public static IReadOnlyList<PlayerStat> Roster()
        {
            var list = new List<PlayerStat>();
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var room = gm?.room?.curRoomInfo;
                if (room == null) return list;

                if (room.Players != null)
                    foreach (var p in room.Players)
                        AddStat(list, gm, p, isMonster: false);
                if (room.Monsters != null)
                    foreach (var m in room.Monsters)
                        AddStat(list, gm, m, isMonster: true);
            }
            catch { }
            return list;
        }

        private static void AddStat(List<PlayerStat> list, GameLogicManager gm, RoomPlayer p, bool isMonster)
        {
            try
            {
                if (p == null) return;
                var prop = p.Property;
                var stat = new PlayerStat
                {
                    Id = p.Id,
                    Name = SafeRoomNick(p),
                    Atk = prop?.ATK?.Value ?? 0,
                    Def = prop?.DEF?.Value ?? 0,
                    Hp = prop?.HP?.Value ?? 0,
                    MaxHp = prop?.maxHP ?? 0,
                    IsMonster = isMonster,
                    IsBot = SafeBot(p),
                    IsSelf = !isMonster && (gm?.account?.IsSelf(p.Id) ?? false),
                };
                list.Add(stat);
            }
            catch { }
        }

        private static string SafeRoomNick(RoomPlayer p)
        {
            try { var n = p?.GetNick(); return string.IsNullOrWhiteSpace(n) ? null : n; }
            catch { return null; }
        }

        private static bool SafeBot(RoomPlayer p)
        {
            try { return p != null && p.IsBot; } catch { return false; }
        }

        /// <summary>把战斗用牌的 cardGuid 反查成真实 CardId(从该玩家手牌容器)。查不到原样返回。</summary>
        public static int ResolveCardGuid(long playerId, int cardGuid)
        {
            try
            {
                var pd = Get(playerId);
                if (pd?.cardContainer?._HandCards != null)
                {
                    foreach (var hc in pd.cardContainer._HandCards)
                    {
                        if (hc != null && hc.Guid == cardGuid && hc.CardId > 0)
                            return hc.CardId;
                    }
                }
            }
            catch { }
            return cardGuid;
        }

        /// <summary>这张手牌 Guid 是否在我手上(战斗出牌发的是 Guid, 用它挡住"给了别的玩家/过期的手牌号")。</summary>
        public static bool HandHasGuid(long playerId, int cardGuid)
        {
            if (cardGuid == 0) return false;
            try
            {
                var pd = Get(playerId);
                if (pd?.cardContainer?._HandCards != null)
                {
                    foreach (var hc in pd.cardContainer._HandCards)
                    {
                        if (hc != null && hc.Guid == cardGuid) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 把卡牌配置 id 反查成手牌的唯一实例号 Guid(战斗用牌走 Guid, 见 <c>GameActions.UseCard</c>)。
        /// 同一张配置可能有多张实例, 返回第一张。查不到返回 0。
        /// </summary>
        public static int ResolveCardIdToGuid(long playerId, int cardId)
        {
            try
            {
                var pd = Get(playerId);
                if (pd?.cardContainer?._HandCards != null)
                {
                    foreach (var hc in pd.cardContainer._HandCards)
                    {
                        if (hc != null && hc.CardId == cardId && hc.Guid > 0) return hc.Guid;
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>一个单位身上的一条激活 buff。</summary>
        public struct BuffOnUnit
        {
            /// <summary>buff 配置 id(对应 Buff.bin / STRBuff)。</summary>
            public int BuffId;
            /// <summary>当前层数 / 进度。取自 <c>Buff.Progress</c>(即 buff 图标上显示的堆叠数)。</summary>
            public int Layers;
            /// <summary>剩余回合数(0 = 不按回合计时或已到期表现层)。</summary>
            public int KeepRound;
        }

        /// <summary>
        /// 读取任意单位(玩家或怪物)当前身上的全部激活 buff, 含层数。
        /// 数据取自该单位 <c>party.model.Hero.Buffs</c>(<c>MapField&lt;long,Buff&gt;</c>)。
        /// 查不到(不在房间/无该单位)返回空。绝不抛异常。
        /// </summary>
        public static IReadOnlyList<BuffOnUnit> BuffsOf(long unitId)
        {
            var list = new List<BuffOnUnit>();
            try
            {
                var hero = HeroOf(unitId);
                if (hero?.Buffs == null) return list;
                foreach (var kv in hero.Buffs)
                {
                    try
                    {
                        var b = kv.Value;
                        if (b == null) continue;
                        int layers = 0;
                        try { layers = b.Progress; } catch { }
                        if (layers <= 0) layers = 1; // 无进度字段的 buff 视为 1 层
                        int keep = 0;
                        try { keep = b.KeepRound; } catch { }
                        list.Add(new BuffOnUnit { BuffId = b.BuffId, Layers = layers, KeepRound = keep });
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        /// <summary>取某单位的 Hero 模型(先查战斗 PlayerDatas, 再查房间 Players+Monsters)。查不到返回 null。</summary>
        private static Hero HeroOf(long unitId)
        {
            try
            {
                var pd = Get(unitId);
                if (pd?.player?.Hero != null) return pd.player.Hero;
            }
            catch { }
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var room = gm?.room?.curRoomInfo;
                if (room != null)
                {
                    if (room.Players != null)
                        foreach (var p in room.Players)
                            if (p != null && p.Id == unitId && p.Hero != null) return p.Hero;
                    if (room.Monsters != null)
                        foreach (var m in room.Monsters)
                            if (m != null && m.Id == unitId && m.Hero != null) return m.Hero;
                }
            }
            catch { }
            return null;
        }
    }
}
