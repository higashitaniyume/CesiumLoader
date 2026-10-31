using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 移速加成读数的拼装口径:
    ///   · 净加成 = 加速 − 减速; 掷骰后的移动点数 = 骰点 + 净加成。
    ///   · 加速与减速同时存在时拆开显示, 如 <c>移速+3（+4/-1）</c>。
    ///   · 只有一侧时只显示净值; 都没有时显示 <c>移速0</c>。
    ///   · 颜色档位按净加成的正负决定。
    /// </summary>
    public class MoveBonusTextTests
    {
        [Fact]
        public void NoBonus_ShowsZero()
        {
            var v = MoveBonusText.Build(0, 0);
            Assert.Equal("移速0", v.Text);
            Assert.Equal(0, v.Net);
            Assert.Equal(MoveBonusTint.Zero, v.Tint);
        }

        [Fact]
        public void PureBonus_ShowsNetOnly()
        {
            var v = MoveBonusText.Build(3, 0);
            Assert.Equal("移速+3", v.Text);
            Assert.Equal(3, v.Net);
            Assert.Equal(MoveBonusTint.Positive, v.Tint);
        }

        [Fact]
        public void PurePenalty_ShowsNegativeNet()
        {
            var v = MoveBonusText.Build(0, 2);
            Assert.Equal("移速-2", v.Text);
            Assert.Equal(-2, v.Net);
            Assert.Equal(MoveBonusTint.Negative, v.Tint);
        }

        [Fact]
        public void BonusAndPenalty_BothSidesShown()
        {
            var v = MoveBonusText.Build(4, 1);
            Assert.Equal("移速+3（+4/-1）", v.Text);
            Assert.Equal(3, v.Net);
            Assert.Equal(MoveBonusTint.Positive, v.Tint);
        }

        [Fact]
        public void PenaltyLargerThanBonus_NetIsNegative()
        {
            var v = MoveBonusText.Build(1, 4);
            Assert.Equal("移速-3（+1/-4）", v.Text);
            Assert.Equal(-3, v.Net);
            Assert.Equal(MoveBonusTint.Negative, v.Tint);
        }

        [Fact]
        public void CancellingValues_NetZeroButKeepsBreakdown()
        {
            var v = MoveBonusText.Build(2, 2);
            Assert.Equal("移速0（+2/-2）", v.Text);
            Assert.Equal(0, v.Net);
            Assert.Equal(MoveBonusTint.Zero, v.Tint);
        }

        [Theory]
        [InlineData(-3, 0)]     // 异常负的加速按 0 处理
        [InlineData(-3, -1)]    // 两者都负 → 都没有加成
        public void NegativeInputs_AreClampedToZero(int add, int minus)
        {
            var v = MoveBonusText.Build(add, minus);
            Assert.Equal("移速0", v.Text);
            Assert.Equal(0, v.Net);
            Assert.Equal(MoveBonusTint.Zero, v.Tint);
        }

        [Fact]
        public void NegativePenaltyInput_IsClampedToZero()
        {
            var v = MoveBonusText.Build(2, -3);
            Assert.Equal("移速+2", v.Text);
            Assert.Equal(2, v.Net);
        }

        [Fact]
        public void LargeValues_ArePrintedVerbatim()
        {
            var v = MoveBonusText.Build(12, 5);
            Assert.Equal("移速+7（+12/-5）", v.Text);
            Assert.Equal(7, v.Net);
        }
    }
}
