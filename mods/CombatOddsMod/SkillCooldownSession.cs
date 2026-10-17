using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>
    /// 技能冷却的会话状态(纯逻辑, 无游戏/SDK 依赖, 可离线单测)。
    ///
    /// 数值只来自服务器, 不做任何本地推算:
    ///   - 查询回包: 每名玩家当前的剩余冷却(绝对值);
    ///   - 释放技能帧: 本次实际生效的冷却上限(= 释放瞬间的剩余冷却)。
    /// 显示分式的分子取前者, 分母取后者, 没有记录时由调用方回退配表上限。
    /// </summary>
    internal sealed class SkillCooldownSession
    {
        private readonly SkillCooldownTable _table = new SkillCooldownTable();
        private readonly Dictionary<long, int> _capByPlayer = new Dictionary<long, int>();
        private readonly Dictionary<long, Dictionary<int, int>> _capByPlayerSkill = new Dictionary<long, Dictionary<int, int>>();

        internal void Reset()
        {
            _table.Clear();
            _capByPlayer.Clear();
            _capByPlayerSkill.Clear();
        }

        /// <summary>查询回包: 该玩家当前的剩余冷却(服务器绝对值)。</summary>
        internal void ApplyQuery(long playerId, int remaining)
        {
            if (playerId == 0) return;
            if (remaining < 0 || !SkillCooldownRules.IsPlausible(remaining)) return;
            _table.SetExact(playerId, remaining);
        }

        /// <summary>
        /// 释放技能帧给出的值: 既是释放瞬间的剩余冷却, 也是本次实际生效的冷却上限
        /// (强化/天赋/遗物会让它区别于配表的 Round)。
        /// </summary>
        internal void ApplyCast(long playerId, int skillId, int applied)
        {
            int value = SkillCooldownRules.Clamp(applied);
            _table.SetExact(playerId, value);
            if (!SkillCooldownRules.IsPlausible(value) || value <= 0) return;
            _capByPlayer[playerId] = value;
            if (skillId == 0) return;
            Dictionary<int, int> bySkill;
            if (!_capByPlayerSkill.TryGetValue(playerId, out bySkill) || bySkill == null)
            {
                bySkill = new Dictionary<int, int>();
                _capByPlayerSkill[playerId] = bySkill;
            }
            bySkill[skillId] = value;
        }

        /// <summary>该玩家当前的剩余冷却; 还没有查询结果时返回 false, 调用方不显示。</summary>
        internal bool TryGetRemaining(long playerId, out int remaining)
        {
            return _table.TryGet(playerId, out remaining);
        }

        /// <summary>
        /// 该玩家这个主动技能最近一次释放时的生效上限; 还没释放过时返回 false(调用方用配表值)。
        /// 优先按(玩家, 技能)取; 技能 ID 未知时才退回该玩家最近一次释放的值。
        /// </summary>
        internal bool TryGetCap(long playerId, int skillId, out int cap)
        {
            if (skillId != 0 && _capByPlayerSkill.TryGetValue(playerId, out var bySkill) && bySkill != null &&
                bySkill.TryGetValue(skillId, out cap) && cap > 0) return true;
            if (skillId == 0 && _capByPlayer.TryGetValue(playerId, out cap) && cap > 0) return true;
            cap = 0;
            return false;
        }

        /// <summary>已维护的玩家数。</summary>
        internal int Count => _table.Count;
    }
}
