using Xunit;

namespace CombatOddsMod.Tests
{
    public class RelicInputCacheTests
    {
        [Fact]
        public void EveryEvaluationInputInvalidatesTheSnapshot()
        {
            var original = new RelicAtkBonus.Input();
            Assert.True(RelicAtkBonus.SameInput(original, original));
            var changed = original; changed.HasCoinHammer = true; Assert.False(RelicAtkBonus.SameInput(original, changed));
            changed = original; changed.HasFlashlight = true; Assert.False(RelicAtkBonus.SameInput(original, changed));
            changed = original; changed.HasKnifeBasic = true; Assert.False(RelicAtkBonus.SameInput(original, changed));
            changed = original; changed.HasKnifeSharp = true; Assert.False(RelicAtkBonus.SameInput(original, changed));
            changed = original; changed.Gold = 1; Assert.False(RelicAtkBonus.SameInput(original, changed));
            changed = original; changed.CureLayers = 1; Assert.False(RelicAtkBonus.SameInput(original, changed));
            changed = original; changed.SalaryLayers = 1; Assert.False(RelicAtkBonus.SameInput(original, changed));
            changed = original; changed.Hp = 1; Assert.False(RelicAtkBonus.SameInput(original, changed));
            changed = original; changed.MaxHp = 1; Assert.False(RelicAtkBonus.SameInput(original, changed));
        }

        [Fact]
        public void SameTotalWithDifferentSourcesStillInvalidatesTheTooltip()
        {
            var coin = new RelicAtkBonus.Input { HasCoinHammer = true, Gold = 24 };
            var flashlight = new RelicAtkBonus.Input { HasFlashlight = true, SalaryLayers = 28 };
            Assert.Equal(7, RelicAtkBonus.Total(RelicAtkBonus.Evaluate(coin)));
            Assert.Equal(7, RelicAtkBonus.Total(RelicAtkBonus.Evaluate(flashlight)));
            Assert.False(RelicAtkBonus.SameInput(coin, flashlight));
        }
    }
}
