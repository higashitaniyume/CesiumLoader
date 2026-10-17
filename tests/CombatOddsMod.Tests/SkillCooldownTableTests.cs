using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 技能冷却表的口径: 只保存服务器给出的绝对值, 按玩家各存一份, 换局清空。
    /// </summary>
    public class SkillCooldownTableTests
    {
        private const long A = 1744079;
        private const long B = 535688;
        private const long C = 3015816;
        private const long D = 2268982;

        [Fact]
        public void UnknownPlayer_HasNoValueUntilSeen()
        {
            var table = new SkillCooldownTable();
            Assert.False(table.TryGet(A, out _));
            table.SetExact(A, 2);
            Assert.True(table.TryGet(A, out int remaining));
            Assert.Equal(2, remaining);
            Assert.Equal(1, table.Count);
        }

        [Fact]
        public void EachPlayerKeepsItsOwnExactValue()
        {
            var table = new SkillCooldownTable();
            table.SetExact(A, 2);
            table.SetExact(B, 2);
            table.SetExact(C, 2);
            table.SetExact(D, 2);
            Assert.Equal(2, Remaining(table, A));
            Assert.Equal(2, Remaining(table, B));
            Assert.Equal(2, Remaining(table, C));
            Assert.Equal(2, Remaining(table, D));
            Assert.Equal(4, table.Count);
        }

        [Fact]
        public void OnePlayersValue_DoesNotTouchTheOthers()
        {
            var table = new SkillCooldownTable();
            table.SetExact(A, 2);
            table.SetExact(B, 1);
            table.SetExact(C, 0);
            Assert.Equal(2, Remaining(table, A));
            Assert.Equal(1, Remaining(table, B));
            Assert.Equal(0, Remaining(table, C));
        }

        [Fact]
        public void ValuesFollowTheServerSequence()
        {
            var table = new SkillCooldownTable();
            int[] sequence = { 2, 1, 0, 2, 1, 0 };
            for (int i = 0; i < sequence.Length; i++)
            {
                table.SetExact(A, sequence[i]);
                Assert.Equal(sequence[i], Remaining(table, A));
            }
        }

        [Fact]
        public void CooldownNeverGoesBelowZero()
        {
            var table = new SkillCooldownTable();
            table.SetExact(B, -3);
            Assert.Equal(0, Remaining(table, B));
        }

        [Fact]
        public void Clear_ResetsEverythingForTheNextBattle()
        {
            var table = new SkillCooldownTable();
            table.SetExact(A, 3);
            table.SetExact(B, 1);
            table.Clear();
            Assert.Equal(0, table.Count);
            Assert.False(table.TryGet(A, out _));
            table.SetExact(A, 2);
            Assert.Equal(2, Remaining(table, A));
        }

        [Fact]
        public void FinalRoundSentinel_IsNotShownAsCooldown()
        {
            // 决赛轮服务器会给每名玩家推 98, 那是结束标记, 不是剩余冷却。
            Assert.False(SkillCooldownRules.IsPlausible(98));
            Assert.False(SkillCooldownRules.IsPlausible(-1));
            Assert.True(SkillCooldownRules.IsPlausible(0));
            Assert.True(SkillCooldownRules.IsPlausible(3));
            Assert.True(SkillCooldownRules.IsPlausible(12));
        }

        private static int Remaining(SkillCooldownTable table, long playerId)
        {
            Assert.True(table.TryGet(playerId, out int remaining), "玩家 " + playerId + " 没有冷却记录");
            return remaining;
        }
    }
}
