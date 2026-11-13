using PvePanelMod;
using Xunit;

namespace PvePanelMod.Tests
{
    /// <summary>右栏第一排的倒计时。规则：刷怪和轮次奖励都在回合开头结算，没有「本回合」；奖励在 3、6、9… 回合。</summary>
    public class RoundMathTests
    {
        [Theory]
        [InlineData(1, 2)]
        [InlineData(2, 1)]   // 下回合！
        [InlineData(3, 3)]   // 第 3 回合开头已经发了，算到第 6 回合
        [InlineData(4, 2)]
        [InlineData(5, 1)]
        [InlineData(6, 3)]
        [InlineData(12, 3)]
        public void RewardCountsToNextMultipleOfThree(int round, int expected)
        {
            Assert.Equal(expected, RoundMath.RewardIn(round));
        }

        [Fact]
        public void RewardNeverSaysThisRound()
        {
            for (int round = 0; round <= 30; round++)
            {
                int n = RoundMath.RewardIn(round);
                Assert.InRange(n, 1, 3);
            }
        }

        [Theory]
        [InlineData(1, 2)]    // 波次在 1、3、7：第 1 回合的那波开头已经刷了，下一波是第 3 回合
        [InlineData(2, 1)]
        [InlineData(3, 4)]
        [InlineData(6, 1)]
        [InlineData(7, -1)]   // 之后没有怪物波次
        public void MonsterCountsOnlyFutureWaves(int round, int expected)
        {
            Assert.Equal(expected, RoundMath.MonsterIn(round, new[] { 1, 3, 7 }));
        }

        [Fact]
        public void MonsterHandlesUnsortedAndEmptyLists()
        {
            Assert.Equal(1, RoundMath.MonsterIn(2, new[] { 7, 3, 1 }));
            Assert.Equal(-1, RoundMath.MonsterIn(2, new int[0]));
            Assert.Equal(-1, RoundMath.MonsterIn(2, null));
        }
    }
}
