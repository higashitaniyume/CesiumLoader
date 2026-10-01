using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 星币锤 / 手电筒 / 美工刀 的攻击力加成计算。
    /// 描述口径(已与用户确认):
    ///   · 星币锤 50043: 星币 &gt; 20 时 +floor(星币 30%), 随后消耗6星币(消耗不由本模块处理)。
    ///   · 手电筒 50013: +floor(星光层数 / 4)。
    ///   · 美工刀 50010/50011: 仅满血时 +2/+4, 且额外 +治愈层数; 两个美工刀各出一个 +N。
    /// </summary>
    public class RelicAtkBonusTests
    {
        private static RelicAtkBonus.Input Base()
        {
            return new RelicAtkBonus.Input
            {
                Gold = 0,
                CureLayers = 0,
                SalaryLayers = 0,
                Hp = 10,
                MaxHp = 10,   // 默认满血
            };
        }

        private static RelicAtkBonus.Bonus Find(RelicAtkBonus.Input i, int relicId)
        {
            return RelicAtkBonus.Evaluate(i).FirstOrDefault(b => b.RelicId == relicId);
        }

        // ============================ 什么都没有 ============================

        [Fact]
        public void NoRelics_YieldsNothing()
        {
            Assert.Empty(RelicAtkBonus.Evaluate(Base()));
            Assert.Equal(0, RelicAtkBonus.Total(RelicAtkBonus.Evaluate(Base())));
        }

        [Fact]
        public void RelicsHeld_ButNoConditionsMet_YieldsNothing()
        {
            var i = Base();
            i.HasCoinHammer = true;    // 星币 0, 不达门槛
            i.HasFlashlight = true;    // 星光 0 层
            i.Hp = 3;                  // 非满血 → 美工刀不生效
            i.HasKnifeBasic = true;
            i.HasKnifeSharp = true;
            Assert.Empty(RelicAtkBonus.Evaluate(i));
        }

        // ============================ 星币锤 ============================

        [Theory]
        [InlineData(20, false)]   // 门槛是"大于20", 20 不触发
        [InlineData(21, true)]
        [InlineData(100, true)]
        public void CoinHammer_ThresholdIsStrictlyGreaterThan20(int gold, bool expected)
        {
            var i = Base();
            i.HasCoinHammer = true;
            i.Gold = gold;
            Assert.Equal(expected, Find(i, RelicAtkBonus.CoinHammerId) != null);
        }

        [Theory]
        [InlineData(21, 6)]      // 6.3 → 6
        [InlineData(23, 7)]      // 6.9 → 7
        [InlineData(25, 8)]      // 7.5 → 8(四舍五入)
        [InlineData(30, 9)]      // 9.0 → 9
        [InlineData(33, 10)]     // 9.9 → 10
        [InlineData(100, 30)]    // ★ 浮点陷阱回归: 100*0.30 在 double 下是 29.999…, 直接取整会得 29
        [InlineData(106, 32)]    // ★ 真机对拍: 31.8 → 32(不是 31)
        [InlineData(112, 34)]    // ★ 真机对拍: 33.6 → 34(不是 33)
        [InlineData(200, 60)]
        public void CoinHammer_IsRoundedPercent(int gold, int expected)
        {
            var i = Base();
            i.HasCoinHammer = true;
            i.Gold = gold;
            Assert.Equal(expected, Find(i, RelicAtkBonus.CoinHammerId).Value);
        }

        [Fact]
        public void CoinHammer_IsYellow()
        {
            var i = Base();
            i.HasCoinHammer = true;
            i.Gold = 50;
            Assert.Equal(RelicAtkBonus.Tint.Yellow, Find(i, RelicAtkBonus.CoinHammerId).Color);
        }

        // ============================ 手电筒 ============================

        [Theory]
        [InlineData(0, false)]
        [InlineData(3, false)]   // 不足 4 层 → 无加成
        [InlineData(4, true)]
        [InlineData(9, true)]
        public void Flashlight_OnlyAtFourOrMoreLayers(int salary, bool expected)
        {
            var i = Base();
            i.HasFlashlight = true;
            i.SalaryLayers = salary;
            Assert.Equal(expected, Find(i, RelicAtkBonus.FlashlightId) != null);
        }

        [Theory]
        [InlineData(4, 1)]
        [InlineData(7, 1)]       // floor(1.75)
        [InlineData(8, 2)]
        [InlineData(9, 2)]
        [InlineData(40, 10)]
        public void Flashlight_IsFloorOfLayersDivFour(int salary, int expected)
        {
            var i = Base();
            i.HasFlashlight = true;
            i.SalaryLayers = salary;
            Assert.Equal(expected, Find(i, RelicAtkBonus.FlashlightId).Value);
        }

        [Fact]
        public void Flashlight_IsYellow()
        {
            var i = Base();
            i.HasFlashlight = true;
            i.SalaryLayers = 8;
            Assert.Equal(RelicAtkBonus.Tint.Yellow, Find(i, RelicAtkBonus.FlashlightId).Color);
        }

        // ============================ 美工刀 ============================

        [Fact]
        public void Knives_RequireFullHp()
        {
            var i = Base();
            i.HasKnifeBasic = true;
            i.HasKnifeSharp = true;
            i.CureLayers = 5;       // 有治愈层数, 但差 1 血 → 都不生效
            i.Hp = 9;
            i.MaxHp = 10;
            Assert.Empty(RelicAtkBonus.Evaluate(i));
        }

        [Fact]
        public void Knives_MaxHpUnknown_DoNotFire()
        {
            var i = Base();
            i.HasKnifeBasic = true;
            i.CureLayers = 5;
            i.Hp = 10;
            i.MaxHp = 0;            // 血量上限未知 → 宁可少显示, 也不误报
            Assert.Empty(RelicAtkBonus.Evaluate(i));
        }

        [Fact]
        public void Knives_DeadPlayer_DoNotFire()
        {
            var i = Base();
            i.HasKnifeBasic = true;
            i.CureLayers = 3;
            i.Hp = 0;
            i.MaxHp = 0;
            Assert.Empty(RelicAtkBonus.Evaluate(i));
        }

        [Theory]
        [InlineData(0, false)]   // 没治愈层数 → 不显示
        [InlineData(3, true)]
        [InlineData(7, true)]
        public void KnifeBasic_OnlyNeedsCureLayers(int cure, bool expected)
        {
            var i = Base();
            i.HasKnifeBasic = true;
            i.CureLayers = cure;
            Assert.Equal(expected, Find(i, RelicAtkBonus.KnifeBasicId) != null);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(3, 3)]
        [InlineData(7, 7)]
        [InlineData(12, 12)]
        public void KnifeBasic_ValueIsCureLayersOnly_NotFlatPlus(int cure, int expected)
        {
            // ★ 真机对拍修正: 描述里的 "+2" 是美工刀自身的攻击力(已在服务器下发的攻击力里),
            //    这里只能补"治愈层数"那部分 —— 加 +2 就是重复计算。
            var i = Base();
            i.HasKnifeBasic = true;
            i.CureLayers = cure;
            Assert.Equal(expected, Find(i, RelicAtkBonus.KnifeBasicId).Value);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(3, 3)]
        [InlineData(7, 7)]
        public void KnifeSharp_ValueIsCureLayersOnly_NotFlatPlus(int cure, int expected)
        {
            var i = Base();
            i.HasKnifeSharp = true;
            i.CureLayers = cure;
            Assert.Equal(expected, Find(i, RelicAtkBonus.KnifeSharpId).Value);
        }

        [Fact]
        public void BothKnives_ProduceTwoSeparateBonuses()
        {
            var i = Base();
            i.HasKnifeBasic = true;
            i.HasKnifeSharp = true;
            i.CureLayers = 2;       // 两把美工刀各 +2(治愈层数), 不是 4 和 6
            var list = RelicAtkBonus.Evaluate(i);
            Assert.Equal(2, list.Count);
            Assert.All(list, b => Assert.Equal(RelicAtkBonus.Tint.Green, b.Color));
            Assert.Equal(new[] { 2, 2 }, list.Select(b => b.Value).ToArray());
            Assert.Equal(4, RelicAtkBonus.Total(list));
        }

        [Fact]
        public void Knives_AreGreen()
        {
            var i = Base();
            i.HasKnifeBasic = true;
            i.CureLayers = 4;
            Assert.Equal(RelicAtkBonus.Tint.Green, Find(i, RelicAtkBonus.KnifeBasicId).Color);
        }

        [Fact]
        public void NegativeCureLayers_DoNotProduceBonus()
        {
            var i = Base();
            i.HasKnifeBasic = true;
            i.CureLayers = -5;      // 脏数据: 不能算出负数加成, 也不能当成有层数
            Assert.Empty(RelicAtkBonus.Evaluate(i));
        }

        // ============================ 组合 / 顺序 / 合计 ============================

        [Fact]
        public void AllFour_FixedOrder_HammerFlashlightKnifeBasicKnifeSharp()
        {
            var i = Base();
            i.HasCoinHammer = true;
            i.HasFlashlight = true;
            i.HasKnifeBasic = true;
            i.HasKnifeSharp = true;
            i.Gold = 100;           // 30
            i.SalaryLayers = 9;     // 2
            i.CureLayers = 5;       // 两把美工刀各 5

            var list = RelicAtkBonus.Evaluate(i);
            Assert.Equal(4, list.Count);
            Assert.Equal(
                new[] { RelicAtkBonus.CoinHammerId, RelicAtkBonus.FlashlightId, RelicAtkBonus.KnifeBasicId, RelicAtkBonus.KnifeSharpId },
                list.Select(b => b.RelicId).ToArray());
            Assert.Equal(30 + 2 + 5 + 5, RelicAtkBonus.Total(list));
        }

        /// <summary>
        /// 真机观测回归(用户实测两组, 结算伤害都精确等于 18 + 加成 = 64):
        ///   星币106/治愈7 → 星币锤 32 + 美工刀 7 + 美工刀 7 = 46 → 18+46 = 64
        ///   星币112/治愈6 → 星币锤 34 + 美工刀 6 + 美工刀 6 = 46 → 18+46 = 64
        /// 这条同时锁死: 星币锤四舍五入(向下取整会得 31/33 → 63, 差 1)、美工刀只算治愈层数。
        /// </summary>
        [Fact]
        public void RealMachineObservation_BothAttacksReachExactly64()
        {
            const int hudAtk = 18;

            var a1 = Base();
            a1.HasCoinHammer = true;
            a1.HasKnifeBasic = true;
            a1.HasKnifeSharp = true;
            a1.Gold = 106;
            a1.CureLayers = 7;
            var l1 = RelicAtkBonus.Evaluate(a1);
            Assert.Equal(32, l1.First(b => b.RelicId == RelicAtkBonus.CoinHammerId).Value);
            Assert.Equal(46, RelicAtkBonus.Total(l1));
            Assert.Equal(64, hudAtk + RelicAtkBonus.Total(l1));

            var a2 = Base();
            a2.HasCoinHammer = true;
            a2.HasKnifeBasic = true;
            a2.HasKnifeSharp = true;
            a2.Gold = 112;
            a2.CureLayers = 6;
            var l2 = RelicAtkBonus.Evaluate(a2);
            Assert.Equal(34, l2.First(b => b.RelicId == RelicAtkBonus.CoinHammerId).Value);
            Assert.Equal(46, RelicAtkBonus.Total(l2));
            Assert.Equal(64, hudAtk + RelicAtkBonus.Total(l2));
        }

        [Fact]
        public void Total_HandlesNull()
        {
            Assert.Equal(0, RelicAtkBonus.Total(null));
        }

        [Fact]
        public void IsBonusRelic_KnowsTheFour()
        {
            Assert.True(RelicAtkBonus.IsBonusRelic(50043));
            Assert.True(RelicAtkBonus.IsBonusRelic(50013));
            Assert.True(RelicAtkBonus.IsBonusRelic(50010));
            Assert.True(RelicAtkBonus.IsBonusRelic(50011));
            Assert.False(RelicAtkBonus.IsBonusRelic(50012));   // 手电筒-一般: Relic.bin 里没有条目
            Assert.False(RelicAtkBonus.IsBonusRelic(50015));   // 银行卡: 不加攻击力
            Assert.False(RelicAtkBonus.IsBonusRelic(0));
        }
    }
}
