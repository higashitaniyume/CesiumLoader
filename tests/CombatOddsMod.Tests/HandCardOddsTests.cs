using System;
using Xunit;

namespace CombatOddsMod.Tests
{
    public class HandCardOddsTests
    {
        [Fact]
        public void AttackPreviewIncludesAllCardAndJudgeRolls()
        {
            var odds = CombatMath.PreviewCardOdds(true, 1, 3, 5, new[] { 6 }, 0, new[] { 6 }, 8);
            Assert.Equal(46.0 / 108, odds, 10);
        }

        [Theory]
        [InlineData(3, 21)]
        [InlineData(5, 30)]
        [InlineData(6, 33)]
        public void FixedAttackBonusStillIncludesBothJudgeDice(int bonus, int winningRolls)
        {
            var odds = CombatMath.PreviewCardOdds(true, bonus, bonus, 5, new[] { 6 }, 0, new[] { 6 }, 8);
            Assert.Equal(winningRolls / 36.0, odds, 10);
        }

        [Theory]
        [InlineData(false, 16, 1.0)]
        [InlineData(true, 17, 0.5)]
        public void PowerfulAttackRoundsUpAfterTheRollAndDoesNotStack(bool alreadyPlayed, int targetHp, double expected)
        {
            // Totals 11 and 12 become 17 and 18, dealing 16 and 17 damage.
            var odds = CombatMath.PreviewCardOdds(true, 6, 6, 4, new[] { 2 }, 1, null, targetHp,
                cardId: CombatMath.PowerfulAttackCardId, powerfulAttackActive: alreadyPlayed);
            Assert.Equal(expected, odds, 10);
        }

        [Theory]
        [InlineData(4, 4)]
        [InlineData(1, 5)]
        [InlineData(0, 5)]
        public void DragonRoarReducesBaseDefenseAndKeepsCardAndJudgeBonuses(int baseDefense, int targetHp)
        {
            var odds = CombatMath.PreviewCardOdds(true, 3, 3, 5, null, baseDefense + 2, new[] { 2 }, targetHp,
                cardId: CombatMath.DragonRoarCardId, baseDefense: baseDefense);
            Assert.Equal(0.5, odds, 10);
        }

        [Fact]
        public void AttackPreviewDoesNotFlattenTheSumOfTwoRandomBonuses()
        {
            var odds = CombatMath.PreviewCardOdds(true, 1, 3, 0, new[] { 3 }, 0, null, 5);
            Assert.Equal(3.0 / 9, odds, 10);
        }

        [Fact]
        public void DefensePreviewUsesSurvivalAndKeepsAnAlreadyRolledAttackFixed()
        {
            var odds = CombatMath.PreviewCardOdds(false, 1, 3, 10, Array.Empty<int>(), 0, new[] { 6 }, 8);
            Assert.Equal(17.0 / 18, odds, 10);
        }

        [Fact]
        public void DamageCapAppliesToThePreview()
        {
            var odds = CombatMath.PreviewCardOdds(true, 1, 3, 20, new[] { 6 }, 0, new[] { 6 }, 8, maxDamage: 7);
            Assert.Equal(0.0, odds);
        }
    }
}
