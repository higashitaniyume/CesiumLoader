using System;
using System.Collections.Generic;
using party.model;
using Tools;
using UI;

namespace CesiumLoader.SDK
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

        /// <summary>卡牌名: 真实名(Id)。查不到时尝试技能表, 最后"未知卡{id}"。</summary>
        public static string Card(int cardId)
        {
            if (cardId <= 0) return $"卡{cardId}";
            lock (_cardCache)
            {
                if (_cardCache.TryGetValue(cardId, out var v)) return v;
                v = ResolveCard(cardId);
                _cardCache[cardId] = v;
                return v;
            }
        }

        private static string ResolveCard(int cardId)
        {
            try
            {
                var cfg = cardId.GetCardConfigure();
                if (cfg != null)
                {
                    string cn = cfg.NameID.GetLocal(UIStringType.Card);
                    if (!string.IsNullOrEmpty(cn)) return cn + $"({cardId})";
                }
            }
            catch { }
            // 卡表查不到: 可能是技能卡, 尝试技能表
            try
            {
                if (StaticConfigure.Skill.InfoDict.TryGetValue(cardId, out var skillCfg))
                {
                    string sn = skillCfg.NameID.GetLocal(UIStringType.Skill);
                    if (!string.IsNullOrEmpty(sn)) return sn + $"({cardId})";
                }
            }
            catch { }
            return $"未知卡{cardId}";
        }

        /// <summary>遗物名: 真实名(Id), 查不到"遗物{id}"。</summary>
        public static string Relic(int relicId)
        {
            if (relicId <= 0) return $"遗物{relicId}";
            lock (_relicCache)
            {
                if (_relicCache.TryGetValue(relicId, out var v)) return v;
                string r = "遗物" + relicId;
                try
                {
                    var cfg = relicId.GetRelicInfoConfigure();
                    if (cfg != null)
                    {
                        string n = cfg.NameID.GetLocal(UIStringType.Relic);
                        if (!string.IsNullOrEmpty(n)) r = n + $"({relicId})";
                    }
                }
                catch { }
                _relicCache[relicId] = r;
                return r;
            }
        }

        /// <summary>技能名: 真实名(Id), 查不到"技能{id}"。</summary>
        public static string Skill(int skillId)
        {
            if (skillId <= 0) return $"技能{skillId}";
            lock (_skillCache)
            {
                if (_skillCache.TryGetValue(skillId, out var v)) return v;
                string s = "技能" + skillId;
                try
                {
                    if (StaticConfigure.Skill.InfoDict.TryGetValue(skillId, out var cfg))
                    {
                        string n = cfg.NameID.GetLocal(UIStringType.Skill);
                        if (!string.IsNullOrEmpty(n)) s = n + $"({skillId})";
                    }
                }
                catch { }
                _skillCache[skillId] = s;
                return s;
            }
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
