using System.Collections.Generic;
using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 筹码提供的常驻移速: 服务器不会把常驻部分作为属性推给客户端, 改由本表补上。
    /// 只登记无条件生效的筹码; 有前置条件的（鸳鸯玉佩、能量回收、跃迁引擎）不猜。
    /// </summary>
    public class RelicMoveBonusTests
    {
        [Fact]
        public void NoRelics_YieldsZero()
        {
            Assert.Empty(RelicMoveBonus.Evaluate(null));
            Assert.Equal(0, RelicMoveBonus.Total(new List<int>()));
        }

        [Theory]
        [InlineData(50004, 1)]   // 速度轮滑-初级
        [InlineData(50005, 2)]   // 速度轮滑-中级
        [InlineData(50006, 4)]   // 速度轮滑-高级
        [InlineData(50054, 2)]   // 梦想号模型
        [InlineData(50090, 2)]   // 永动机
        public void KnownRelic_ContributesItsValue(int relicId, int expected)
        {
            var bonuses = RelicMoveBonus.Evaluate(new List<int> { relicId });
            Assert.Single(bonuses);
            Assert.Equal(expected, bonuses[0].Value);
            Assert.Equal(relicId, bonuses[0].RelicId);
            Assert.Equal(expected, RelicMoveBonus.Total(new List<int> { relicId }));
        }

        [Fact]
        public void MultipleMoveRelics_SumUp()
        {
            var ids = new List<int> { 50005, 50090, 50006 };
            Assert.Equal(2 + 2 + 4, RelicMoveBonus.Total(ids));
            Assert.Equal(3, RelicMoveBonus.Evaluate(ids).Count);
        }

        [Fact]
        public void NonMoveRelics_AreIgnored()
        {
            var ids = new List<int> { 50002, 50043, 50083 };   // 拳击手套 / 星币锤 / 循环往复
            Assert.Empty(RelicMoveBonus.Evaluate(ids));
            Assert.Equal(0, RelicMoveBonus.Total(ids));
        }

        [Fact]
        public void ConditionalRelics_AreNotGuessed()
        {
            // 鸳鸯玉佩 / 能量回收 / 跃迁引擎 都有前置条件, 交给投骰差值体现, 不在这里硬加。
            var ids = new List<int> { 50068, 50084, 50088 };
            Assert.Equal(0, RelicMoveBonus.Total(ids));
        }
    }

    /// <summary>
    /// 投骰前的预测值:
    ///   · 已学到数值且仍挂在身上的移速 Buff(含正负)优先;
    ///   · 没有已知 Buff 时用筹码常驻移速(刚买到、还没投过骰也能显示);
    ///   · 服务器刚推的临时加减速比以上更大时以推送为准。
    /// </summary>
    public class MoveBonusPredictTests
    {
        [Fact]
        public void NothingKnown_IsZero()
        {
            Assert.Equal(0, MoveBonusText.Predict(0, 0, knownRelics: 0, trackedBuffMove: 0));
        }

        [Fact]
        public void OnlyRelics_ArePredictedImmediately()
        {
            // 刚买到速度轮滑-中级, 还没投过骰。
            Assert.Equal(2, MoveBonusText.Predict(0, 0, knownRelics: 2, trackedBuffMove: 0));
        }

        [Fact]
        public void TrackedBuffWithPenalty_WinsOverRelicTable()
        {
            // 学到的 Buff 合计 +1(速度轮滑 +2 与减速 -1), 此时筹码表只按 +2 算, 应改用 Buff 合计。
            Assert.Equal(1, MoveBonusText.Predict(0, 0, knownRelics: 2, trackedBuffMove: 1));
        }

        [Fact]
        public void ServerPushLargerThanTracked_Wins()
        {
            // 服务器刚推了 +5(定向加速), 已知 Buff 合计只有 +2。
            Assert.Equal(5, MoveBonusText.Predict(5, 0, knownRelics: 2, trackedBuffMove: 2));
        }

        [Fact]
        public void ServerPushPenalty_DoesNotHideTrackedBonus()
        {
            // 只推了 -1、但已知 Buff 合计是 +2 时仍显示 +2; 减速会体现在下一次 Buff 合计里。
            Assert.Equal(2, MoveBonusText.Predict(0, 1, knownRelics: 0, trackedBuffMove: 2));
        }

        [Fact]
        public void NegativeTrackedBuff_IsShown()
        {
            Assert.Equal(-2, MoveBonusText.Predict(0, 0, knownRelics: 2, trackedBuffMove: -2));
        }

        [Fact]
        public void PredictionEqualsPush_ShowsBreakdown()
        {
            var v = MoveBonusText.BuildPrediction(3, 4, 1);
            Assert.Equal("移速+3（+4/-1）", v.Text);
            Assert.Equal(3, v.Net);
        }

        [Fact]
        public void PredictionDiffersFromPush_ShowsPlainTotal()
        {
            var v = MoveBonusText.BuildPrediction(2, 0, 0);
            Assert.Equal("移速+2", v.Text);
            Assert.Equal(MoveBonusTint.Positive, v.Tint);

            var zero = MoveBonusText.BuildPrediction(0, 0, 0);
            Assert.Equal("移速0", zero.Text);
            Assert.Equal(MoveBonusTint.Zero, zero.Tint);

            var negative = MoveBonusText.BuildPrediction(-1, 0, 0);
            Assert.Equal("移速-1", negative.Text);
            Assert.Equal(MoveBonusTint.Negative, negative.Tint);
        }
    }
}
