using System.Collections.Generic;
using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 技能冷却表的规则测试。真实数据来自回放解码出的 S2C 帧(13 轮 × 4 名玩家):
    /// 每轮每名玩家各收到一帧 RoundStartS2C, 帧内 SkillCds 只带该玩家自己的主动技能键,
    /// 例如 {11403:2}、{11802:1}, 就绪时为 {}。这里覆盖取值规则与"按玩家写绝对值"的口径。
    /// </summary>
    public class SkillCooldownTableTests
    {
        private const long A = 1744079;
        private const long B = 535688;
        private const long C = 3015816;
        private const long D = 2268982;

        private static Dictionary<int, int> Cds(params int[] pairs)
        {
            var map = new Dictionary<int, int>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) map[pairs[i]] = pairs[i + 1];
            return map;
        }

        // ---------- 帧取值规则 ----------

        [Fact]
        public void ResolveOwnerCooldown_EmptyFrameMeansReady()
        {
            // 回放里就绪的帧是空表: {}(第 4 轮四人全是空表)。
            Assert.Equal(0, SkillCooldownRules.ResolveOwnerCooldown(11403, Cds()));
            Assert.Equal(0, SkillCooldownRules.ResolveOwnerCooldown(0, null));
        }

        [Fact]
        public void ResolveOwnerCooldown_UsesMatchingSkillEntry()
        {
            // 帧内只带归属者自己的键: 11403 属于玩家 A, 11802 属于玩家 B。
            Assert.Equal(2, SkillCooldownRules.ResolveOwnerCooldown(11403, Cds(11403, 2)));
            Assert.Equal(1, SkillCooldownRules.ResolveOwnerCooldown(12602, Cds(12602, 1)));
        }

        [Fact]
        public void ResolveOwnerCooldown_FallsBackToSingleEntryWhenSkillUnknown()
        {
            // PVE 强化/天赋可能让客户端算出的技能 ID 与服务器键不同, 该帧唯一的一条仍属于归属者。
            Assert.Equal(2, SkillCooldownRules.ResolveOwnerCooldown(11401, Cds(11403, 2)));
            Assert.Equal(0, SkillCooldownRules.ResolveOwnerCooldown(0, Cds(11403, 0)));
        }

        [Fact]
        public void ResolveOwnerCooldown_UnknownSkillAmongManyEntriesIsReady()
        {
            Assert.Equal(0, SkillCooldownRules.ResolveOwnerCooldown(11403, Cds(10202, 3, 12702, 2)));
        }

        [Fact]
        public void ResolveOwnerCooldown_ClampsNegative()
        {
            Assert.Equal(0, SkillCooldownRules.ResolveOwnerCooldown(11403, Cds(11403, -5)));
        }

        // ---------- 决赛轮哨兵 ----------

        [Fact]
        public void FinalRoundSentinel_IsNotShownAsCooldown()
        {
            // 回放里第 13 轮(决赛轮)四名玩家同时收到 {技能:98}, 那不是剩余冷却。
            Assert.False(SkillCooldownRules.IsPlausible(98));
            Assert.False(SkillCooldownRules.IsPlausible(-1));
            // 正常范围(含被效果加长的冷却)照常显示。
            Assert.True(SkillCooldownRules.IsPlausible(0));
            Assert.True(SkillCooldownRules.IsPlausible(3));
            Assert.True(SkillCooldownRules.IsPlausible(12));
        }

        // ---------- 表的写入口径 ----------

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
            // 第 2 轮: A 的帧 {11403:2}, B 的帧 {11802:2}, C 的帧 {12602:2}, D 的帧 {11503:2}。
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
        public void LaterFrameOfAnotherPlayer_DoesNotTouchTheOthers()
        {
            var table = new SkillCooldownTable();
            table.SetExact(A, 2);
            table.SetExact(B, 1);
            // 游戏客户端把同一帧套给所有玩家, 队友会被刷成 0; 这里只写归属者。
            table.SetExact(C, 0);
            Assert.Equal(2, Remaining(table, A));
            Assert.Equal(1, Remaining(table, B));
            Assert.Equal(0, Remaining(table, C));
        }

        [Fact]
        public void RoundValuesFollowTheServerSequence()
        {
            var table = new SkillCooldownTable();
            // 玩家 A 的第 2~7 轮: 2, 1, {}, 2, 1, {}。
            int[] sequence = { 2, 1, 0, 2, 1, 0 };
            for (int i = 0; i < sequence.Length; i++)
            {
                table.SetExact(A, sequence[i]);
                Assert.Equal(sequence[i], Remaining(table, A));
            }
        }

        [Fact]
        public void CooldownCorrection_OverridesTheFrameValue()
        {
            var table = new SkillCooldownTable();
            table.SetExact(B, 3);
            // 过载模式/遗物把冷却修正为 1: 修正值本身就是剩余冷却。
            table.SetExact(B, 1);
            Assert.Equal(1, Remaining(table, B));
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
            // 新一局从服务器第一帧重新开始。
            table.SetExact(A, 2);
            Assert.Equal(2, Remaining(table, A));
        }

        /// <summary>
        /// 回放 1792075040252960(PVE, 4 人, 13 轮)里 1015 帧的原始序列:
        /// 每轮四帧, 顺序为 A/B/C/D, 帧内只带该玩家自己的主动技能键, 0 表示空表(就绪)。
        /// 按帧顺序写入后, 每个玩家在每轮的显示值必须与服务器给该玩家的值完全一致。
        /// </summary>
        private static readonly int[][] ReplayRounds =
        {
            new[] { 0, 0, 0, 0 },
            new[] { 2, 2, 2, 2 },
            new[] { 1, 1, 1, 1 },
            new[] { 0, 0, 0, 0 },
            new[] { 2, 2, 1, 0 },
            new[] { 1, 1, 0, 0 },
            new[] { 0, 0, 1, 2 },
            new[] { 2, 2, 0, 1 },
            new[] { 1, 1, 1, 0 },
            new[] { 0, 0, 0, 2 },
            new[] { 2, 2, 1, 1 },
            new[] { 0, 1, 0, 0 },
            new[] { 2, 0, 1, 0 },
        };

        [Fact]
        public void RealReplayFrames_ReproduceTheServerValuesForEveryPlayer()
        {
            var table = new SkillCooldownTable();
            var players = new[] { A, B, C, D };
            var skills = new[] { 11403, 11802, 12602, 11503 };

            for (int round = 0; round < ReplayRounds.Length; round++)
            {
                var frame = ReplayRounds[round];
                for (int slot = 0; slot < players.Length; slot++)
                {
                    // 帧内只含归属者自己的技能键; 值为 0 时服务器发的是空表。
                    var cds = frame[slot] == 0
                        ? new Dictionary<int, int>()
                        : Cds(skills[slot], frame[slot]);
                    table.SetExact(players[slot], SkillCooldownRules.ResolveOwnerCooldown(skills[slot], cds));
                }

                for (int slot = 0; slot < players.Length; slot++)
                {
                    Assert.True(table.TryGet(players[slot], out int remaining));
                    Assert.Equal(frame[slot], remaining);
                }
            }
        }

        private static int Remaining(SkillCooldownTable table, long playerId)
        {
            Assert.True(table.TryGet(playerId, out int remaining), "玩家 " + playerId + " 没有冷却记录");
            return remaining;
        }
    }
}
