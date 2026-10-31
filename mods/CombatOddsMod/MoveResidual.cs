using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>
    /// 学习"客户端算不出来、但每次投骰都会稳定多/少出来的那部分"。
    ///
    /// 背景: 建筑效果（疾行菇等）、角色隐藏被动、累计类效果不会作为属性或 Buff 下发给客户端，
    /// 无论表补得多全都会有遗漏。但这类常驻来源的特点是 **连续多次投骰的差额一致**，所以可以在
    /// 投骰时记下 `实际加成 − 当时预测`，连续两次相同就把它当作常驻修正加到之后的预测上；
    /// 一次性效果（只出现一次的临时加成）不会连续两次相同，因此不会被长期保留。
    ///
    /// 修正会随每次投骰重新学习, 所以 Buff 变化、换筹码后会自动跟上。
    /// 纯逻辑(零游戏依赖), 由 CombatOddsMod.Tests 直接编译测试。
    /// </summary>
    internal static class MoveResidual
    {
        private class State
        {
            public int LastResidual = int.MinValue;
            public int StableCount;
            public int Learned;
        }

        private static readonly Dictionary<long, State> States = new Dictionary<long, State>();

        /// <summary>记录一次投骰的差额, 返回本次是否被认定为稳定修正。</summary>
        internal static bool Learn(long playerId, int actualDelta, int predictedBase)
        {
            int residual = actualDelta - predictedBase;
            State state;
            if (!States.TryGetValue(playerId, out state))
            {
                state = new State();
                States[playerId] = state;
            }

            if (residual == state.LastResidual)
            {
                state.StableCount++;
            }
            else
            {
                state.LastResidual = residual;
                state.StableCount = 1;
            }

            if (state.StableCount >= 2)
            {
                state.Learned = residual;
                return true;
            }
            return false;
        }

        /// <summary>当前学到的常驻修正(没有则 0)。</summary>
        internal static int Get(long playerId)
        {
            State state;
            return States.TryGetValue(playerId, out state) ? state.Learned : 0;
        }

        /// <summary>把学到的修正加到预测上。</summary>
        internal static int Adjust(long playerId, int predictedBase)
        {
            return predictedBase + Get(playerId);
        }

        internal static void Reset()
        {
            States.Clear();
        }
    }
}
