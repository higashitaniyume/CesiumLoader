using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>
    /// 技能全局冷却的会话状态(纯逻辑, 无游戏/SDK 依赖, 可离线单测)。
    ///
    /// 只接收服务器给出的绝对值, 不做本地推算:
    ///   - 回合开始帧: 每名玩家一帧, 帧内只带该玩家自己的主动技能键, 缺键表示就绪(0);
    ///   - 释放技能帧: 帧内的值就是本次生效的冷却上限, 同时作为该玩家的剩余冷却;
    ///   - 冷却修正: 广播的目标玩家 + 绝对值。
    ///
    /// 取不到玩家 ID 对应的值时, 若该技能 ID 在本局唯一, 再按技能键兜底
    /// (归属者 ID 与游戏内玩家 ID 口径不一致时仍能显示)。
    /// </summary>
    internal sealed class SkillCooldownSession
    {
        private readonly SkillCooldownTable _table = new SkillCooldownTable();
        private readonly Dictionary<int, int> _lastBySkill = new Dictionary<int, int>();
        private readonly Dictionary<long, int> _capByPlayer = new Dictionary<long, int>();
        private readonly Dictionary<int, int> _capBySkill = new Dictionary<int, int>();
        private readonly Dictionary<long, int> _skillByPlayer = new Dictionary<long, int>();
        private readonly HashSet<int> _sharedSkills = new HashSet<int>();

        internal void Reset()
        {
            _table.Clear();
            _lastBySkill.Clear();
            _capByPlayer.Clear();
            _capBySkill.Clear();
            _skillByPlayer.Clear();
            _sharedSkills.Clear();
        }

        /// <summary>多名玩家共用同一主动技能 ID 时不再按技能键兜底, 避免串号。</summary>
        internal void SetSharedSkills(IEnumerable<int> skillIds)
        {
            _sharedSkills.Clear();
            if (skillIds == null) return;
            foreach (var skillId in skillIds) _sharedSkills.Add(skillId);
        }

        /// <summary>
        /// 写入一帧冷却消息(回合开始帧): 帧内只带归属者自己的主动技能键, 缺键表示就绪。
        /// </summary>
        internal void ApplyFrame(long ownerPlayerId, int ownerSkillId, IEnumerable<KeyValuePair<int, int>> cooldowns)
        {
            if (cooldowns == null) return;
            int skillId = ownerSkillId;
            int count = 0;
            int singleKey = 0;
            int singleValue = 0;
            foreach (var pair in cooldowns)
            {
                count++;
                singleKey = pair.Key;
                singleValue = pair.Value;
            }
            if (skillId == 0 && count == 1) skillId = singleKey;

            int remaining = SkillCooldownRules.ResolveOwnerCooldown(skillId, cooldowns);
            _table.SetExact(ownerPlayerId, remaining);
            if (count != 1) return;
            _lastBySkill[singleKey] = SkillCooldownRules.Clamp(singleValue);
        }

        /// <summary>
        /// 释放技能后游戏写入释放者的值: 既是剩余冷却, 也是本次实际生效的冷却上限
        /// (强化/天赋/遗物会让它区别于配表的 Round)。
        /// </summary>
        internal void ApplyCast(long playerId, int applied)
        {
            int value = SkillCooldownRules.Clamp(applied);
            _table.SetExact(playerId, value);
            if (!SkillCooldownRules.IsPlausible(value) || value <= 0) return;
            _capByPlayer[playerId] = value;
            int skillId;
            if (!_skillByPlayer.TryGetValue(playerId, out skillId)) return;
            _capBySkill[skillId] = value;
        }

        /// <summary>记录玩家当前的主动技能 ID, 供按技能键兜底与上限归并使用。</summary>
        internal void SetPlayerSkill(long playerId, int skillId)
        {
            if (playerId == 0 || skillId == 0) return;
            _skillByPlayer[playerId] = skillId;
        }

        /// <summary>冷却修正(天赋/遗物/地图效果)广播的绝对值。</summary>
        internal void ApplyCorrection(long playerId, int remaining)
        {
            _table.SetExact(playerId, remaining);
        }

        /// <summary>该玩家当前的剩余冷却; 无数据时返回 false, 调用方不显示。</summary>
        internal bool TryGetRemaining(long playerId, int skillId, out int remaining)
        {
            if (_table.TryGet(playerId, out remaining)) return true;
            if (skillId != 0 && !_sharedSkills.Contains(skillId) &&
                _lastBySkill.TryGetValue(skillId, out remaining)) return true;
            remaining = 0;
            return false;
        }

        /// <summary>该玩家最近一次释放技能时的生效上限; 还没释放过时返回 false。</summary>
        internal bool TryGetCap(long playerId, int skillId, out int cap)
        {
            if (_capByPlayer.TryGetValue(playerId, out cap) && cap > 0) return true;
            if (skillId != 0 && !_sharedSkills.Contains(skillId) &&
                _capBySkill.TryGetValue(skillId, out cap) && cap > 0) return true;
            cap = 0;
            return false;
        }

        /// <summary>已维护的玩家数。</summary>
        internal int Count => _table.Count;
    }
}
