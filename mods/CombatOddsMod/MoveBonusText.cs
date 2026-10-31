using System;

namespace CombatOddsMod
{
    /// <summary>移速加成读数的颜色档位(只描述正负, 具体颜色由覆盖层决定)。</summary>
    internal enum MoveBonusTint
    {
        Positive,
        Negative,
        Zero,
    }

    /// <summary>移速加成的一条读数: 显示文本、净加成与颜色档位。</summary>
    internal struct MoveBonusView
    {
        public string Text;
        public int Net;
        public MoveBonusTint Tint;
    }

    /// <summary>
    /// 把服务器下发的「加速 / 减速」点数拼成 HUD 上的一行读数。
    ///
    /// 口径(与游戏自己的棋盘名牌同源):
    ///   · <c>BattleProperty.ExtraAddMovePoint</c> = 累计加速, <c>ExtraMinusMovePoint</c> = 累计减速。
    ///   · 净加成 = 加速 − 减速; 掷骰后的移动点数 = 骰点 + 净加成。
    ///   · 读数形如 <c>移速+3（+4/-1）</c>: 加速与减速都有值时把两侧分开显示, 只有一侧时只显示净值,
    ///     都没有时显示灰色的 <c>移速0</c>。
    ///
    /// 纯逻辑(零游戏依赖), 由 CombatOddsMod.Tests 直接编译测试。
    /// </summary>
    internal static class MoveBonusText
    {
        /// <summary>读数前缀。</summary>
        internal const string Label = "移速";

        internal static MoveBonusView Build(int extraAdd, int extraMinus)
        {
            // 服务器语义里两者都是非负累计值; 异常负值按 0 处理, 免得出现 "--1" 这种读数。
            if (extraAdd < 0) extraAdd = 0;
            if (extraMinus < 0) extraMinus = 0;

            int net = extraAdd - extraMinus;
            string text = Label + Signed(net);

            // 只有加速和减速同时存在时才值得拆开显示; 单侧的值已经等于净值, 括号只是噪音。
            if (extraAdd > 0 && extraMinus > 0)
                text += "（+" + extraAdd + "/-" + extraMinus + "）";

            return new MoveBonusView
            {
                Text = text,
                Net = net,
                Tint = net > 0 ? MoveBonusTint.Positive
                     : net < 0 ? MoveBonusTint.Negative
                     : MoveBonusTint.Zero,
            };
        }

        private static string Signed(int value)
        {
            if (value > 0) return "+" + value;
            return value.ToString();
        }

        /// <summary>只有总加成时的读数(例如来自"最近一次投骰"的实际差值)。</summary>
        internal static MoveBonusView BuildTotal(int total)
        {
            return new MoveBonusView
            {
                Text = Label + Signed(total),
                Net = total,
                Tint = total > 0 ? MoveBonusTint.Positive
                     : total < 0 ? MoveBonusTint.Negative
                     : MoveBonusTint.Zero,
            };
        }

        /// <summary>
        /// 投骰前的预测值:
        ///   · 已经学到数值、并且仍挂在玩家身上的移速 Buff 合计;
        ///   · 服务器刚推的临时加减速(AddMove)比它大时以推送为准;
        ///   · 都没有已知 Buff 时退回筹码常驻移速(刚买到、还没投过骰也能显示)。
        /// </summary>
        internal static int Predict(int extraAdd, int extraMinus, int knownRelics, int trackedBuffMove)
        {
            int propertyNet = Normalize(extraAdd) - Normalize(extraMinus);

            int predicted = trackedBuffMove != 0 ? trackedBuffMove : knownRelics;
            // 服务器推了正加成时以它为准(可能包含还没学到的 Buff); 0 表示"没推送/不确定", 不能覆盖负数。
            if (propertyNet != 0 && propertyNet > predicted) predicted = propertyNet;
            return predicted;
        }

        /// <summary>预测值的读数(预测值与推送总量一致时附上加速/减速分解)。</summary>
        internal static MoveBonusView BuildPrediction(int predicted, int extraAdd, int extraMinus)
        {
            int propertyNet = Normalize(extraAdd) - Normalize(extraMinus);
            if (predicted == propertyNet && extraAdd > 0 && extraMinus > 0) return Build(extraAdd, extraMinus);
            return BuildTotal(predicted);
        }

        private static int Normalize(int value)
        {
            return value < 0 ? 0 : value;
        }
    }
}
