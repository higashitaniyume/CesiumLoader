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
    }
}
