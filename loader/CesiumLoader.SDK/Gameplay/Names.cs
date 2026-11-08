using System;
using System.Collections.Generic;
using party.model;
using Tools;
using UI;

namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 游戏内名字解析: 卡牌 / 遗物 / 技能 / 角色 / 战斗角色。
    /// 全部带缓存 + 查不到时的明确 fallback。
    /// </summary>
    public static class Names
    {
        private static readonly Dictionary<int, string> _cardCache = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _relicCache = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _skillCache = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _charCache = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _divinationCache = new Dictionary<int, string>();

        private static readonly Dictionary<int, string> _cardNameCache = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _relicNameCache = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _skillNameCache = new Dictionary<int, string>();

        /// <summary>卡牌名: 真实名(Id)。查不到时尝试技能表, 最后"未知卡{id}"。</summary>
        public static string Card(int cardId) => Card(cardId, withId: true);

        /// <summary>卡牌名: <paramref name="withId"/> 为真时带 "(id)", 为假只返回名字。</summary>
        public static string Card(int cardId, bool withId)
        {
            if (cardId <= 0) return $"卡{cardId}";
            if (withId)
            {
                lock (_cardCache)
                {
                    if (_cardCache.TryGetValue(cardId, out var v)) return v;
                    v = ResolveCard(cardId, withId: true);
                    _cardCache[cardId] = v;
                    return v;
                }
            }
            lock (_cardNameCache)
            {
                if (_cardNameCache.TryGetValue(cardId, out var v)) return v;
                v = ResolveCard(cardId, withId: false);
                _cardNameCache[cardId] = v;
                return v;
            }
        }

        private static string ResolveCard(int cardId, bool withId)
        {
            try
            {
                var cfg = cardId.GetCardConfigure();
                if (cfg != null)
                {
                    string cn = cfg.NameID.GetLocal(UIStringType.Card);
                    if (!string.IsNullOrEmpty(cn)) return withId ? cn + $"({cardId})" : cn;
                }
            }
            catch { }
            // 卡表查不到: 可能是技能卡, 尝试技能表
            try
            {
                if (StaticConfigure.Skill.InfoDict.TryGetValue(cardId, out var skillCfg))
                {
                    string sn = skillCfg.NameID.GetLocal(UIStringType.Skill);
                    if (!string.IsNullOrEmpty(sn)) return withId ? sn + $"({cardId})" : sn;
                }
            }
            catch { }
            return withId ? $"未知卡{cardId}" : "未知卡牌";
        }

        /// <summary>遗物名: 真实名(Id), 查不到"遗物{id}"。</summary>
        public static string Relic(int relicId) => Relic(relicId, withId: true);

        /// <summary>遗物名: <paramref name="withId"/> 为真时带 "(id)", 为假只返回名字。</summary>
        public static string Relic(int relicId, bool withId)
        {
            if (relicId <= 0) return $"遗物{relicId}";
            if (withId)
            {
                lock (_relicCache)
                {
                    if (_relicCache.TryGetValue(relicId, out var v)) return v;
                    v = ResolveRelic(relicId, withId: true);
                    _relicCache[relicId] = v;
                    return v;
                }
            }
            lock (_relicNameCache)
            {
                if (_relicNameCache.TryGetValue(relicId, out var v)) return v;
                v = ResolveRelic(relicId, withId: false);
                _relicNameCache[relicId] = v;
                return v;
            }
        }

        private static string ResolveRelic(int relicId, bool withId)
        {
            string fallback = withId ? ("遗物" + relicId) : "未知筹码";
            try
            {
                var cfg = relicId.GetRelicInfoConfigure();
                if (cfg != null)
                {
                    string n = cfg.NameID.GetLocal(UIStringType.Relic);
                    if (!string.IsNullOrEmpty(n)) return withId ? n + $"({relicId})" : n;
                    return fallback;
                }
            }
            catch { }
            return fallback;
        }

        /// <summary>占卜牌名: 真实名(Id), 查不到"占卜{id}"。</summary>
        public static string Divination(int divinationId)
        {
            if (divinationId <= 0) return $"占卜{divinationId}";
            lock (_divinationCache)
            {
                if (_divinationCache.TryGetValue(divinationId, out var v)) return v;
                string d = "占卜" + divinationId;
                try
                {
                    if (StaticConfigure.Divination.InfoDict.TryGetValue(divinationId, out var cfg))
                    {
                        string n = cfg.NameID.GetLocal(UIStringType.Divination);
                        if (!string.IsNullOrEmpty(n)) d = n + $"({divinationId})";
                    }
                }
                catch { }
                _divinationCache[divinationId] = d;
                return d;
            }
        }

        /// <summary>技能名: 真实名(Id), 查不到"技能{id}"。</summary>
        public static string Skill(int skillId) => Skill(skillId, withId: true);

        /// <summary>技能名: <paramref name="withId"/> 为真时带 "(id)", 为假只返回名字。</summary>
        public static string Skill(int skillId, bool withId)
        {
            if (skillId <= 0) return $"技能{skillId}";
            if (withId)
            {
                lock (_skillCache)
                {
                    if (_skillCache.TryGetValue(skillId, out var v)) return v;
                    v = ResolveSkill(skillId, withId: true);
                    _skillCache[skillId] = v;
                    return v;
                }
            }
            lock (_skillNameCache)
            {
                if (_skillNameCache.TryGetValue(skillId, out var v)) return v;
                v = ResolveSkill(skillId, withId: false);
                _skillNameCache[skillId] = v;
                return v;
            }
        }

        private static string ResolveSkill(int skillId, bool withId)
        {
            string fallback = withId ? ("技能" + skillId) : "未知技能";
            try
            {
                if (StaticConfigure.Skill.InfoDict.TryGetValue(skillId, out var cfg))
                {
                    string n = cfg.NameID.GetLocal(UIStringType.Skill);
                    if (!string.IsNullOrEmpty(n)) return withId ? n + $"({skillId})" : n;
                    return fallback;
                }
            }
            catch { }
            return fallback;
        }

        /// <summary>角色/怪物名: 用 CharacterHandle 查, 查不到返回 id。</summary>
        public static string Character(int characterId)
        {
            if (characterId <= 0) return $"角色{characterId}";
            lock (_charCache)
            {
                if (_charCache.TryGetValue(characterId, out var v)) return v;
                string c = "角色" + characterId;
                try
                {
                    string n = CharacterHandle.GetCharacterName(characterId);
                    if (!string.IsNullOrEmpty(n)) c = n;
                }
                catch { }
                _charCache[characterId] = c;
                return c;
            }
        }

        /// <summary>战斗角色名: 玩家查昵称, 怪物用 HeroId 查名, 最后 "P{PlayerId}"。</summary>
        public static string BattleRole(BattleRole role)
        {
            if (role == null) return "?";
            string nick = Players.Nick(role.PlayerId);
            if (!string.IsNullOrEmpty(nick)) return nick;
            if (role.HeroId != 0)
            {
                try
                {
                    string mn = CharacterHandle.GetCharacterName(role.HeroId, CharacterType.Monster);
                    if (!string.IsNullOrEmpty(mn)) return mn;
                }
                catch { }
            }
            return "P" + role.PlayerId;
        }
    }
}
