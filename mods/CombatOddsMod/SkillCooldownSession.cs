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
        private readonly Dictionary<long, Dictionary<int, int>> _capByPlayerSkill = new Dictionary<long, Dictionary<int, int>>();
        private readonly Dictionary<long, int> _skillByPlayer = new Dictionary<long, int>();
        private readonly HashSet<int> _sharedSkills = new HashSet<int>();
        /// <summary>最近一次已知的轮次(来自本人的回合帧)。</summary>
        private int _round;
        /// <summary>每名玩家最后被本地 -1 的轮次, 保证一轮只扣一次。</summary>
        private readonly Dictionary<long, int> _tickedRound = new Dictionary<long, int>();
        /// <summary>被"轮次推进兜底"扣过的轮次(这种扣法不占该玩家本轮的回合)。</summary>
        private readonly Dictionary<long, int> _fallbackTick = new Dictionary<long, int>();

        internal void Reset()
        {
            _table.Clear();
            _lastBySkill.Clear();
            _capByPlayer.Clear();
            _capBySkill.Clear();
            _capByPlayerSkill.Clear();
            _skillByPlayer.Clear();
            _sharedSkills.Clear();
            _tickedRound.Clear();
            _round = 0;
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
        /// <paramref name="round"/> 为帧里的轮次, 用来给没收到帧的队友按轮次递减。
        /// </summary>
        internal void ApplyFrame(long ownerPlayerId, int ownerSkillId, IEnumerable<KeyValuePair<int, int>> cooldowns, int round = 0)
        {
            if (cooldowns == null) return;
            if (round > 0) AdvanceRound(round);
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
            BumpCap(ownerPlayerId, skillId, remaining);
            if (count != 1) return;
            _lastBySkill[singleKey] = SkillCooldownRules.Clamp(singleValue);
        }

        /// <summary>
        /// 轮次前进: 给"上一轮和本轮都没轮到过"的玩家兜底 -1。
        /// 正常玩家会在自己的回合开始被 <see cref="OnTurnStart"/> 扣; 这里只兜住漏掉回合的人
        /// (观战/被跳过/回合信号丢失), 不抢在别人回合之前扣。
        /// </summary>
        private void AdvanceRound(int round)
        {
            if (round <= _round) return;
            _round = round;
            var players = _table.Players;
            for (int i = 0; i < players.Count; i++)
            {
                long playerId = players[i];
                int ticked;
                if (_tickedRound.TryGetValue(playerId, out ticked) && ticked >= round - 1) continue;
                if (Tick(playerId, round)) _fallbackTick[playerId] = round;
            }
        }

        /// <summary>
        /// 某名玩家的回合开始: 服务器在这一刻把他的剩余冷却 -1(本人会同时收到 1015 精确帧)。
        /// 每名玩家每轮只有一次回合, 所以:
        ///   · 本轮还没扣过 → 记到当前轮;
        ///   · 本轮已经扣过(说明他的上个回合在更早的轮次) → 这一定是下一轮的回合, 记到 轮次+1;
        ///   · 本轮是被"轮次推进兜底"扣的 → 这一次就是本轮的回合, 不重复扣。
        /// 这样在他自己的回合开始就立即 -1, 和服务器时机一致。
        /// </summary>
        internal void OnTurnStart(long playerId)
        {
            if (_round <= 0) return;
            int ticked;
            if (_tickedRound.TryGetValue(playerId, out ticked) && ticked >= _round)
            {
                int fallback;
                if (_fallbackTick.TryGetValue(playerId, out fallback) && fallback == ticked) return;
                Tick(playerId, _round + 1);
                return;
            }
            Tick(playerId, _round);
        }

        /// <summary>给该玩家 -1(每轮只扣一次)。返回是否真的扣了。</summary>
        private bool Tick(long playerId, int round)
        {
            int ticked;
            if (_tickedRound.TryGetValue(playerId, out ticked) && ticked >= round) return false;
            int value;
            if (!_table.TryGet(playerId, out value)) return false;
            _tickedRound[playerId] = round;
            if (value > 0) _table.SetExact(playerId, value - 1);
            return true;
        }

        /// <summary>
        /// 服务器给出的剩余值不可能超过该玩家这次的上限。只有在**已经知道上限**时,
        /// 才把观测到的更大值当成新上限(漏掉了释放帧、遗物抬高上限、或换了技能);
        /// 从未释放过的玩家不会被一帧 2 反向猜成"上限 2"。
        /// </summary>
        private void BumpCap(long playerId, int skillId, int remaining)
        {
            if (remaining <= 0) return;
            if (skillId != 0)
            {
                Dictionary<int, int> bySkill;
                if (_capByPlayerSkill.TryGetValue(playerId, out bySkill) && bySkill != null)
                {
                    int known;
                    if (bySkill.TryGetValue(skillId, out known) && remaining > known) bySkill[skillId] = remaining;
                }
                int knownSkill;
                if (!_sharedSkills.Contains(skillId) &&
                    _capBySkill.TryGetValue(skillId, out knownSkill) && remaining > knownSkill)
                    _capBySkill[skillId] = remaining;
            }
            int playerCap;
            if (_capByPlayer.TryGetValue(playerId, out playerCap) && remaining > playerCap)
                _capByPlayer[playerId] = remaining;
        }

        /// <summary>
        /// 释放技能后游戏写入释放者的值: 既是剩余冷却, 也是本次实际生效的冷却上限
        /// (强化/天赋/遗物会让它区别于配表的 Round)。skillId 来自释放消息本身,
        /// 不依赖 <c>_skillByPlayer</c> 是否已经填好。
        /// </summary>
        internal void ApplyCast(long playerId, int skillId, int applied)
        {
            int value = SkillCooldownRules.Clamp(applied);
            _table.SetExact(playerId, value);
            if (!SkillCooldownRules.IsPlausible(value) || value <= 0) return;
            _capByPlayer[playerId] = value;
            if (skillId == 0) return;
            if (!_sharedSkills.Contains(skillId)) _capBySkill[skillId] = value;
            Dictionary<int, int> bySkill;
            if (!_capByPlayerSkill.TryGetValue(playerId, out bySkill) || bySkill == null)
            {
                bySkill = new Dictionary<int, int>();
                _capByPlayerSkill[playerId] = bySkill;
            }
            bySkill[skillId] = value;
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

        /// <summary>
        /// 该玩家这个主动技能最近一次释放时的生效上限; 还没释放过时返回 false。
        /// 优先按(玩家, 技能)取 —— 换技能后不会沿用旧技能的上限; 取不到再退回该玩家
        /// 最近一次释放的值, 最后才用本局唯一技能键兜底。
        /// </summary>
        internal bool TryGetCap(long playerId, int skillId, out int cap)
        {
            if (skillId != 0 && _capByPlayerSkill.TryGetValue(playerId, out var bySkill) && bySkill != null &&
                bySkill.TryGetValue(skillId, out cap) && cap > 0) return true;
            if (skillId != 0 && !_sharedSkills.Contains(skillId) &&
                _capBySkill.TryGetValue(skillId, out cap) && cap > 0) return true;
            // 技能 ID 未知时才退回"该玩家最近一次释放"的上限; 已知技能但从没释放过时
            // 宁可让调用方用配表值, 也不要拿另一个技能的上限来充数。
            if (skillId == 0 && _capByPlayer.TryGetValue(playerId, out cap) && cap > 0) return true;
            cap = 0;
            return false;
        }

        /// <summary>已维护的玩家数。</summary>
        internal int Count => _table.Count;
    }
}
