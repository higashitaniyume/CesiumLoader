using System.Collections.Generic;

namespace PvePanelMod
{
    /// <summary>
    /// 右栏第一排的两个倒计时（纯计算，不碰游戏，单测见 tests/PvePanelMod.Tests）。
    /// 刷怪和轮次奖励都在回合开头、所有操作之前结算，所以「本回合」的已经发生了，只看以后的（没有「本回合」这个状态）。
    /// </summary>
    internal static class RoundMath
    {
        /// <summary>轮次奖励从第 3 回合起每 3 回合一次（3、6、9…）。返回还差几回合（1 = 下回合，最大 3）。</summary>
        public static int RewardIn(int round)
        {
            int r = round < 0 ? 0 : round;
            return 3 - r % 3;
        }

        /// <summary>下一波怪物还差几回合；当前回合之后没有怪物波次返回 -1。</summary>
        public static int MonsterIn(int round, IEnumerable<int> waveRounds)
        {
            int best = -1;
            if (waveRounds == null) return best;
            foreach (int w in waveRounds)
                if (w > round && (best < 0 || w - round < best)) best = w - round;
            return best;
        }
    }
}
