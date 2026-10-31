using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 差额学习：连续两次投骰的"实际 − 预测"一致时，把它当作客户端看不见的常驻来源
    /// （建筑、隐藏被动等）加到预测上；只出现一次的临时差额不会被长期保留。
    /// </summary>
    public class MoveResidualTests
    {
        private const long Player = 1001;

        [Fact]
        public void SingleObservation_IsNotLearned()
        {
            MoveResidual.Reset();
            Assert.False(MoveResidual.Learn(Player, actualDelta: 4, predictedBase: 2));
            Assert.Equal(0, MoveResidual.Get(Player));
            Assert.Equal(2, MoveResidual.Adjust(Player, 2));
        }

        [Fact]
        public void TwoIdenticalObservations_AreLearned()
        {
            MoveResidual.Reset();
            Assert.False(MoveResidual.Learn(Player, 4, 2));
            Assert.True(MoveResidual.Learn(Player, 6, 4));   // 差额仍是 +2
            Assert.Equal(2, MoveResidual.Get(Player));
            Assert.Equal(6, MoveResidual.Adjust(Player, 4));
        }

        [Fact]
        public void OneOffTemporaryBonus_DoesNotStick()
        {
            MoveResidual.Reset();
            MoveResidual.Learn(Player, 4, 2);   // 临时 +2
            MoveResidual.Learn(Player, 2, 2);   // 下一次差额 0
            MoveResidual.Learn(Player, 2, 2);   // 再下一次差额 0 → 学到 0
            Assert.Equal(0, MoveResidual.Get(Player));
        }

        [Fact]
        public void NewStableResidual_ReplacesOldOne()
        {
            MoveResidual.Reset();
            MoveResidual.Learn(Player, 4, 2);
            MoveResidual.Learn(Player, 4, 2);
            Assert.Equal(2, MoveResidual.Get(Player));

            MoveResidual.Learn(Player, 6, 2);   // 差额变 +4(只一次, 暂不采用)
            Assert.Equal(2, MoveResidual.Get(Player));

            MoveResidual.Learn(Player, 6, 2);   // 再次 +4 → 采用
            Assert.Equal(4, MoveResidual.Get(Player));
            Assert.Equal(5, MoveResidual.Adjust(Player, 1));
        }

        [Fact]
        public void PlayersAreLearnedIndependently()
        {
            MoveResidual.Reset();
            MoveResidual.Learn(1, 4, 2);
            MoveResidual.Learn(1, 4, 2);
            MoveResidual.Learn(2, 1, 1);
            MoveResidual.Learn(2, 1, 1);
            Assert.Equal(2, MoveResidual.Get(1));
            Assert.Equal(0, MoveResidual.Get(2));
        }

        [Fact]
        public void Reset_ClearsEverything()
        {
            MoveResidual.Reset();
            MoveResidual.Learn(Player, 4, 2);
            MoveResidual.Learn(Player, 4, 2);
            Assert.Equal(2, MoveResidual.Get(Player));

            MoveResidual.Reset();
            Assert.Equal(0, MoveResidual.Get(Player));
        }
    }
}
