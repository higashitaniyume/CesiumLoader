using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>服务器冷却消息的取值规则(纯函数, 无游戏/SDK 依赖, 可离线单测)。</summary>
    internal static class SkillCooldownRules
    {
        /// <summary>
        /// 取 <c>RoundStartS2C</c> / <c>UseEffectCardS2C</c> 里 <c>SkillCds</c> 中该玩家的剩余冷却。
        /// 实测该表按技能 ID 记录, 每帧只带归属者自己的一条: 命中技能 ID 就取该条,
        /// 取不到技能 ID(PVE 强化或天赋改用变体技能)时按唯一一条兜底, 其余情况视为 0(就绪)。
        /// </summary>
        internal static int ResolveOwnerCooldown(int ownerSkillId, IEnumerable<KeyValuePair<int, int>> cooldowns)
        {
            if (cooldowns == null) return 0;
            int single = 0;
            int count = 0;
            foreach (var pair in cooldowns)
            {
                count++;
                if (ownerSkillId != 0 && pair.Key == ownerSkillId) return Clamp(pair.Value);
                single = pair.Value;
            }
            return count == 1 ? Clamp(single) : 0;
        }

        internal static int Clamp(int remaining)
        {
            return remaining < 0 ? 0 : remaining;
        }

        /// <summary>
        /// 服务器给的是可显示的真实剩余冷却。实测决赛轮会给每名玩家推送 98,
        /// 那是结束标记而不是冷却回合, 超出这个范围的数值一律不显示。
        /// </summary>
        internal static bool IsPlausible(int remaining)
        {
            return remaining >= 0 && remaining <= MaxPlausible;
        }

        internal const int MaxPlausible = 30;
    }

    /// <summary>
    /// 按玩家维护的主动技能剩余冷却。0 表示就绪, 与 <c>Character.activeSkillVail</c> 口径一致。
    ///
    /// 只保存服务器给的绝对值, 不做本地推算: 每轮开始时服务器为每个玩家各发一帧
    /// <c>RoundStartS2C</c>, 帧内 <c>SkillCds</c> 只带该玩家自己的主动技能键, 所以逐个写精确值即可。
    /// 游戏自己的 <c>Property.activeSkillCD</c> 不能用于显示: 客户端会把同一帧套给所有玩家,
    /// 非归属者按自己的技能 ID 查不到键, 被逐帧刷成 0, 一帧发完只有最后一名玩家还留着数字。
    /// </summary>
    internal sealed class SkillCooldownTable
    {
        private readonly Dictionary<long, int> _remaining = new Dictionary<long, int>();

        internal int Count => _remaining.Count;

        internal void Clear()
        {
            _remaining.Clear();
        }

        internal bool TryGet(long playerId, out int remaining)
        {
            return _remaining.TryGetValue(playerId, out remaining);
        }

        /// <summary>服务器给出的精确剩余冷却(回合开始帧、释放技能的帧、冷却修正)。</summary>
        internal void SetExact(long playerId, int remaining)
        {
            _remaining[playerId] = SkillCooldownRules.Clamp(remaining);
        }
    }
}
