using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>服务器冷却消息的取值规则(纯函数, 无游戏/SDK 依赖, 可离线单测)。</summary>
    internal static class SkillCooldownRules
    {
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

    /// <summary>按玩家维护的主动技能剩余冷却(只保存服务器给出的绝对值)。</summary>
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

        internal void SetExact(long playerId, int remaining)
        {
            _remaining[playerId] = SkillCooldownRules.Clamp(remaining);
        }
    }
}
