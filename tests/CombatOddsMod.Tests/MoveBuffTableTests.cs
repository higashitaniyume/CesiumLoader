using System.Collections.Generic;
using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 会改变移速的 Buff 表：
    ///   · 筹码 Buff（速度轮滑等）是固定值；
    ///   · 技能/道具 Buff 取配表文本里写明数值的条目；
    ///   · 糖糖开大这类随层数变化的用 SpecialValue 现算；
    ///   · 服务器教过的数值优先于本地表。
    /// </summary>
    public class MoveBuffTableTests
    {
        private static ActiveBuff B(int buffId, int layers = 1) => new ActiveBuff { BuffId = buffId, Layers = layers };

        [Fact]
        public void NoBuffs_IsZero()
        {
            Assert.Equal(0, MoveBuffTable.Sum(null, null));
            Assert.Equal(0, MoveBuffTable.Sum(new List<ActiveBuff>(), null));
        }

        [Fact]
        public void SkateRelicBuff_IsCounted()
        {
            var buffs = new List<ActiveBuff> { B(MoveBuffTable.SkateMidBuffId) };
            Assert.Equal(2, MoveBuffTable.Sum(buffs, null));

            buffs.Add(B(MoveBuffTable.SkateHighBuffId));
            Assert.Equal(6, MoveBuffTable.Sum(buffs, null));
        }

        [Fact]
        public void DebuffBuff_IsCountedAsNegative()
        {
            var buffs = new List<ActiveBuff> { B(1120101) };   // 移动速度-3
            Assert.Equal(-3, MoveBuffTable.Sum(buffs, null));
        }

        [Fact]
        public void Sugar_UltMoveEqualsHerLayerBuff()
        {
            // 糖糖开大：移速 = 层数 Buff(3021101) 的层数。
            var buffs = new List<ActiveBuff>
            {
                B(MoveBuffTable.SugarMoveBuffId),
                B(MoveBuffTable.SugarLayerBuffId, layers: 4),
            };
            Assert.Equal(4, MoveBuffTable.Sum(buffs, null));

            buffs[1] = B(MoveBuffTable.SugarLayerBuffId, layers: 7);
            Assert.Equal(7, MoveBuffTable.Sum(buffs, null));
        }

        [Fact]
        public void Sugar_WithoutLayerBuff_IsZero()
        {
            var buffs = new List<ActiveBuff> { B(MoveBuffTable.SugarMoveBuffId) };
            Assert.Equal(0, MoveBuffTable.Sum(buffs, null));
        }

        [Theory]
        [InlineData(8, 0)]
        [InlineData(9, 2)]
        [InlineData(12, 2)]
        public void LayerConditionalBuff_NeedsNineLayers(int layers, int expected)
        {
            var buffs = new List<ActiveBuff> { B(MoveBuffTable.LayerConditionalBuffId, layers) };
            Assert.Equal(expected, MoveBuffTable.Sum(buffs, null));
        }

        [Theory]
        [InlineData(5, 0)]
        [InlineData(6, 1)]
        public void EnergyRecycleBuff_NeedsMoreThanFiveLayers(int layers, int expected)
        {
            var buffs = new List<ActiveBuff> { B(MoveBuffTable.EnergyRecycleBuffId, layers) };
            Assert.Equal(expected, MoveBuffTable.Sum(buffs, null));
        }

        [Fact]
        public void JadeToken_NeedsJadePair()
        {
            var without = new List<ActiveBuff> { B(MoveBuffTable.JadeTokenBuffId) };
            Assert.Equal(0, MoveBuffTable.Sum(without, null));

            var with = new List<ActiveBuff> { B(MoveBuffTable.JadeTokenBuffId), B(MoveBuffTable.JadePairBuffId) };
            Assert.Equal(2, MoveBuffTable.Sum(with, null));
        }

        [Fact]
        public void LearnedValueOverridesLocalTable()
        {
            var buffs = new List<ActiveBuff> { B(MoveBuffTable.SkateMidBuffId) };
            var learned = new Dictionary<int, int> { { MoveBuffTable.SkateMidBuffId, 5 } };
            Assert.Equal(5, MoveBuffTable.Sum(buffs, learned));
        }

        [Fact]
        public void UnknownBuff_IsIgnored()
        {
            var buffs = new List<ActiveBuff> { B(999999) };
            Assert.Equal(0, MoveBuffTable.Sum(buffs, null));
        }

        [Fact]
        public void MultipleBuffs_SumTogether()
        {
            var buffs = new List<ActiveBuff>
            {
                B(MoveBuffTable.SkateMidBuffId),   // +2
                B(3031102),                        // +3 改变生活
                B(1120301),                        // -3
                B(5008802),                        // +2 下次移动
            };
            Assert.Equal(2 + 3 - 3 + 2, MoveBuffTable.Sum(buffs, null));
        }

        [Fact]
        public void SkillPassiveAndAfterUltBuffs_AreCounted()
        {
            Assert.Equal(1, MoveBuffTable.Sum(new List<ActiveBuff> { B(10031101) }, null));      // 被动：移速+1
            Assert.Equal(3, MoveBuffTable.Sum(new List<ActiveBuff> { B(1170101) }, null));       // 技能后：移速+3
            Assert.Equal(3, MoveBuffTable.Sum(new List<ActiveBuff> { B(1170102) }, null));
        }
    }
}
