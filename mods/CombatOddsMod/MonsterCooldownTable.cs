using System;
using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>
    /// 怪物主动技能冷却的本地推算表(纯逻辑, 无游戏/SDK 依赖, 可离线单测)。
    ///
    /// 数据来源与限制(回放 + 反编译实测):
    ///   · 怪物释放技能时, 服务器在 <c>UseEffectCardS2C.SkillCds</c> 里下发本次生效的冷却上限,
    ///     客户端随后写入该怪物的 <c>Property.activeSkillCD</c>;
    ///   · 之后每轮的剩余值<b>不会</b>推给普通客户端: 1015 回合帧只发给英雄, 1040 的
    ///     <c>Cd</c> 也只指向英雄, 怪物刷新 1018 的 <c>SkillCds</c> 恒为空;
    ///   · 观战快照 <c>Room.Monsters[].Hero.SkillCds</c> 证明服务器按轮 -1、到 0 省略键。
    ///
    /// 因此这里用"基准轮 + 基准剩余"推算, 每次释放技能重新校准; 刚出现的怪物按就绪起步
    /// (remaining=0), 怪物死亡或离场时由调用方移除。冷却上限(分母)优先取释放时服务器给的值,
    /// 没有时用配表 Round。
    /// </summary>
    internal sealed class MonsterCooldownTable
    {
        private sealed class Entry
        {
            public int SkillId;
            public int Cap;
            public int BaseRemaining;
            public int BaseRound;
        }

        private readonly Dictionary<long, Entry> _entries = new Dictionary<long, Entry>();

        internal int Count => _entries.Count;

        internal void Clear()
        {
            _entries.Clear();
        }

        internal bool Has(long playerId)
        {
            return _entries.ContainsKey(playerId);
        }

        /// <summary>
        /// 怪物释放技能: <paramref name="applied"/> 是本次实际生效的冷却(服务器下发),
        /// <paramref name="round"/> 是释放时所在轮次。不可信的值(98 哨兵等)直接忽略。
        /// </summary>
        internal void ApplyCast(long playerId, int skillId, int applied, int round)
        {
            if (playerId == 0 || applied <= 0 || !SkillCooldownRules.IsPlausible(applied)) return;
            _entries[playerId] = new Entry
            {
                SkillId = skillId,
                Cap = applied,
                BaseRemaining = applied,
                BaseRound = NormalizeRound(round),
            };
        }

        /// <summary>
        /// 用已知剩余值建条目(进房/重连时 <c>Hero.SkillCds</c> 的快照)。
        /// <paramref name="cap"/> 是配表/上次释放的上限, 只用于显示分母。
        /// </summary>
        internal void Seed(long playerId, int skillId, int remaining, int cap, int round)
        {
            if (playerId == 0) return;
            int value = remaining < 0 ? 0 : remaining;
            if (value > SkillCooldownRules.MaxPlausible) return;
            int total = cap > value ? cap : value;
            if (total > SkillCooldownRules.MaxPlausible) return;
            _entries[playerId] = new Entry
            {
                SkillId = skillId,
                Cap = total,
                BaseRemaining = value,
                BaseRound = NormalizeRound(round),
            };
        }

        /// <summary>该怪物在当前轮次的剩余冷却; 没有记录时返回 false(调用方不显示)。</summary>
        internal bool TryGet(long playerId, int round, out int remaining, out int cap)
        {
            Entry entry;
            if (!_entries.TryGetValue(playerId, out entry))
            {
                remaining = 0;
                cap = 0;
                return false;
            }
            int elapsed = round > entry.BaseRound ? round - entry.BaseRound : 0;
            remaining = entry.BaseRemaining - elapsed;
            if (remaining < 0) remaining = 0;
            cap = entry.Cap > remaining ? entry.Cap : remaining;
            return true;
        }

        internal void Remove(long playerId)
        {
            _entries.Remove(playerId);
        }

        private static int NormalizeRound(int round)
        {
            return round > 0 ? round : 1;
        }
    }
}
