using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 怪物主动技能冷却: 服务器只在释放时给本次值, 之后按轮 -1 由本地推算。
    /// </summary>
    public class MonsterCooldownTableTests
    {
        private const long MONSTER = 57562;

        [Fact]
        public void UnknownMonster_HasNoValue()
        {
            var table = new MonsterCooldownTable();
            Assert.False(table.Has(MONSTER));
            Assert.False(table.TryGet(MONSTER, 3, out _, out _));
            Assert.Equal(0, table.Count);
        }

        [Fact]
        public void Cast_StartsAtAppliedValue_AndDropsOnePerRound()
        {
            var table = new MonsterCooldownTable();
            table.ApplyCast(MONSTER, 104301, 3, 5);

            Assert.True(table.TryGet(MONSTER, 5, out int remaining, out int cap));
            Assert.Equal(3, remaining);
            Assert.Equal(3, cap);

            Assert.True(table.TryGet(MONSTER, 6, out remaining, out cap));
            Assert.Equal(2, remaining);
            Assert.Equal(3, cap);

            Assert.True(table.TryGet(MONSTER, 7, out remaining, out cap));
            Assert.Equal(1, remaining);

            Assert.True(table.TryGet(MONSTER, 8, out remaining, out cap));
            Assert.Equal(0, remaining);
            Assert.Equal(3, cap);

            // 到 0 之后保持 0, 不会变成负数。
            Assert.True(table.TryGet(MONSTER, 12, out remaining, out cap));
            Assert.Equal(0, remaining);
            Assert.Equal(3, cap);
        }

        [Fact]
        public void Recast_ResetsTheCountdown()
        {
            var table = new MonsterCooldownTable();
            table.ApplyCast(MONSTER, 104301, 3, 1);
            Assert.Equal(2, Remaining(table, 2));

            table.ApplyCast(MONSTER, 104301, 3, 4);
            Assert.Equal(3, Remaining(table, 4));
            Assert.Equal(2, Remaining(table, 5));
        }

        [Fact]
        public void Seed_UsesKnownRemaining_ButKeepsConfiguredCapAsDenominator()
        {
            // 进房/重连时 Hero.SkillCds 只给当前剩余值, 配表 Round 仍作为分母。
            var table = new MonsterCooldownTable();
            table.Seed(MONSTER, 107301, 2, 3, 4);

            Assert.True(table.TryGet(MONSTER, 4, out int remaining, out int cap));
            Assert.Equal(2, remaining);
            Assert.Equal(3, cap);

            Assert.Equal(1, Remaining(table, 5));
            Assert.Equal(0, Remaining(table, 6));
        }

        [Fact]
        public void Seed_ZeroMeansReady()
        {
            var table = new MonsterCooldownTable();
            table.Seed(MONSTER, 107301, 0, 2, 7);
            Assert.True(table.TryGet(MONSTER, 7, out int remaining, out int cap));
            Assert.Equal(0, remaining);
            Assert.Equal(2, cap);
        }

        [Fact]
        public void SentinelAndInvalidValues_AreIgnored()
        {
            var table = new MonsterCooldownTable();
            // 98 是终局哨兵, 不是真实冷却。
            table.ApplyCast(MONSTER, 104301, 98, 5);
            Assert.False(table.Has(MONSTER));

            table.ApplyCast(MONSTER, 104301, 2, 5);
            // 非法值不能覆盖已有的正确记录。
            table.ApplyCast(MONSTER, 104301, 98, 6);
            Assert.Equal(2, Remaining(table, 5));

            table.ApplyCast(MONSTER, 104301, 0, 7);
            Assert.Equal(1, Remaining(table, 6));
        }

        [Fact]
        public void CapNeverShrinksBelowRemaining()
        {
            var table = new MonsterCooldownTable();
            table.Seed(MONSTER, 107301, 4, 2, 3);
            Assert.True(table.TryGet(MONSTER, 3, out int remaining, out int cap));
            Assert.Equal(4, remaining);
            Assert.Equal(4, cap);
        }

        [Fact]
        public void RemoveAndClear_ForgetTheMonster()
        {
            var table = new MonsterCooldownTable();
            table.ApplyCast(MONSTER, 104301, 3, 1);
            Assert.True(table.Has(MONSTER));
            table.Remove(MONSTER);
            Assert.False(table.Has(MONSTER));

            table.ApplyCast(MONSTER, 104301, 3, 1);
            table.Clear();
            Assert.Equal(0, table.Count);
            Assert.False(table.TryGet(MONSTER, 1, out _, out _));
        }

        private static int Remaining(MonsterCooldownTable table, int round)
        {
            Assert.True(table.TryGet(MONSTER, round, out int remaining, out _), "怪物没有冷却记录");
            return remaining;
        }
    }
}
