using System.Collections.Generic;
using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 技能冷却会话(生产代码 SkillCooldownTracker 内部使用的那一层)的离线测试。
    /// 数据取自真实回放解码出的 S2C 帧:
    ///   - 1015 回合开始帧: 每轮每名玩家一帧, 帧内只带自己的主动技能键(如 {11403:2}, 就绪为 {});
    ///   - 5056 释放技能帧: 帧内是该次实际生效的冷却上限(如 {11403:3}、{30502:2});
    ///   - 1040 冷却修正: 广播的目标玩家 + 绝对值;
    ///   - 1113 快照: 服务端真值, 用于核对每轮结果。
    /// </summary>
    public class SkillCooldownSessionTests
    {
        private const long A = 1184313;
        private const long B = 535688;
        private const long C = 2185070;
        private const long D = 1744079;

        private static readonly long[] Owners = { A, B, C, D };
        private static readonly int[] Skills = { 11403, 30502, 30602, 12902 };

        /// <summary>回放里 1015 帧逐轮的值(顺序 A/B/C/D), 0 表示该帧为空表(就绪)。</summary>
        private static readonly int[][] RoundStartValues =
        {
            new[] { 0, 0, 0, 0 },
            new[] { 2, 0, 0, 2 },
            new[] { 1, 2, 1, 1 },
            new[] { 0, 1, 0, 0 },
            new[] { 2, 0, 0, 2 },
            new[] { 1, 1, 0, 1 },
            new[] { 0, 1, 1, 2 },
            new[] { 2, 0, 0, 1 },
            new[] { 1, 1, 1, 2 },
            new[] { 0, 0, 0, 1 },
            new[] { 2, 1, 1, 0 },
            new[] { 1, 0, 0, 2 },
        };

        private static Dictionary<int, int> Frame(int skillId, int value)
        {
            return value == 0 ? new Dictionary<int, int>() : new Dictionary<int, int> { [skillId] = value };
        }

        [Fact]
        public void RoundStartFrames_GiveEveryPlayerItsOwnValue()
        {
            var session = new SkillCooldownSession();
            for (int round = 0; round < RoundStartValues.Length; round++)
            {
                var values = RoundStartValues[round];
                for (int slot = 0; slot < Owners.Length; slot++)
                    session.ApplyFrame(Owners[slot], Skills[slot], Frame(Skills[slot], values[slot]));

                for (int slot = 0; slot < Owners.Length; slot++)
                {
                    Assert.True(session.TryGetRemaining(Owners[slot], Skills[slot], out int remaining),
                        $"玩家 {Owners[slot]} 第 {round + 1} 轮没有冷却值");
                    Assert.Equal(values[slot], remaining);
                }
            }
        }

        [Fact]
        public void AnotherPlayersFrame_DoesNotChangeTheOthers()
        {
            var session = new SkillCooldownSession();
            session.ApplyFrame(A, Skills[0], Frame(Skills[0], 2));
            session.ApplyFrame(B, Skills[1], Frame(Skills[1], 1));
            // 再来一帧只有 C 的空表: A/B 的数字必须原样保留(游戏客户端此时会把它们刷成 0)。
            session.ApplyFrame(C, Skills[2], Frame(Skills[2], 0));
            Assert.Equal(2, Remaining(session, A, Skills[0]));
            Assert.Equal(1, Remaining(session, B, Skills[1]));
            Assert.Equal(0, Remaining(session, C, Skills[2]));
        }

        [Fact]
        public void SkillCastFrame_SetsRemainingAndTheEffectiveCap()
        {
            var session = new SkillCooldownSession();
            // 回放 f#1488: 玩家 1184313 释放 11403, 帧内 {11403:3} —— 与配表 Round=3 一致。
            session.SetPlayerSkill(A, 11403);
            session.ApplyCast(A, 11403, 3);
            Assert.Equal(3, Remaining(session, A, 11403));
            Assert.True(session.TryGetCap(A, 11403, out int cap));
            Assert.Equal(3, cap);
        }

        [Fact]
        public void SkillCastFrame_CapCanDifferFromTheConfigValue()
        {
            var session = new SkillCooldownSession();
            // 回放 f#1925: 玩家 535688 释放 30502, 帧内 {30502:2}; 配表 Round=3, 该玩家实际上限是 2。
            session.SetPlayerSkill(B, 30502);
            session.ApplyCast(B, 30502, 2);
            Assert.Equal(2, Remaining(session, B, 30502));
            Assert.True(session.TryGetCap(B, 30502, out int cap));
            Assert.Equal(2, cap);
        }

        [Fact]
        public void RoundStartFrame_DoesNotBecomeTheCap()
        {
            var session = new SkillCooldownSession();
            session.ApplyFrame(A, 11403, Frame(11403, 2));
            Assert.False(session.TryGetCap(A, 11403, out _));
        }

        [Fact]
        public void Cap_IsScopedPerSkill_AfterASkillSwitch()
        {
            var session = new SkillCooldownSession();
            // 先放旧技能(上限 3), 换技能后放新技能(上限 2)。
            session.ApplyCast(A, 11403, 3);
            session.ApplyCast(A, 30502, 2);
            Assert.True(session.TryGetCap(A, 30502, out int newCap));
            Assert.Equal(2, newCap);
            Assert.True(session.TryGetCap(A, 11403, out int oldCap));
            Assert.Equal(3, oldCap);
            // 从没释放过的技能不回退到别的技能的上限, 由调用方用配表值。
            Assert.False(session.TryGetCap(A, 99999, out _));
        }

        [Fact]
        public void ObservedRemainingAboveKnownCap_RaisesTheCap()
        {
            var session = new SkillCooldownSession();
            session.ApplyCast(A, 11403, 3);
            // 漏掉了抬高上限的释放帧, 下一帧给出 5: 上限必须跟着抬, 不能显示 "5/3"。
            session.ApplyFrame(A, 11403, Frame(11403, 5));
            Assert.True(session.TryGetCap(A, 11403, out int cap));
            Assert.Equal(5, cap);
            Assert.Equal(5, Remaining(session, A, 11403));
        }

        [Fact]
        public void ObservedRemainingWithoutAnyCast_DoesNotInventACap()
        {
            var session = new SkillCooldownSession();
            session.ApplyFrame(A, 11403, Frame(11403, 2));
            Assert.False(session.TryGetCap(A, 11403, out _));
        }

        [Fact]
        public void Text_NeverShowsRemainingGreaterThanCap()
        {
            Assert.Equal("CD 2/3", SkillCooldownText.Format(2, 3));
            Assert.Equal("CD 0/2", SkillCooldownText.Format(0, 2));
            Assert.Equal("CD 3/3", SkillCooldownText.Format(3, 2));   // 上限还没学到时以剩余为准
            Assert.Equal("CD 0/0", SkillCooldownText.Format(0, 0));
            Assert.Equal("CD 0/3", SkillCooldownText.Format(-1, 3));
        }

        [Fact]
        public void TurnStart_DecrementsThatPlayer()
        {
            var session = new SkillCooldownSession();
            // 服务器只为本人推送回合帧: 队友 B 的剩余由"轮到 B 了"这个信号递减。
            session.ApplyFrame(A, 11403, Frame(11403, 0), 2);   // 已知轮次
            session.ApplyCast(B, 30502, 3);
            session.OnTurnStart(C);                 // 别人回合不影响 B
            Assert.Equal(3, Remaining(session, B, 30502));
            session.OnTurnStart(B);
            Assert.Equal(2, Remaining(session, B, 30502));
            session.ApplyFrame(A, 11403, Frame(11403, 0), 3);
            Assert.Equal(2, Remaining(session, B, 30502));   // 不抢在他回合之前扣
            session.OnTurnStart(B);                          // 下一轮的回合
            Assert.Equal(1, Remaining(session, B, 30502));
        }

        [Fact]
        public void RoundAdvance_DecrementsTeammatesWhoHadNoTurnYet()
        {
            var session = new SkillCooldownSession();
            session.ApplyCast(C, 30602, 3);
            session.ApplyFrame(A, 11403, Frame(11403, 0), 2);
            Assert.Equal(2, Remaining(session, C, 30602));   // 轮次推进兜底
            session.OnTurnStart(C);                          // 他这一轮的回合已扣过
            Assert.Equal(2, Remaining(session, C, 30602));
        }

        [Fact]
        public void TurnStart_DoesNotResurrectAPlayerWithoutData()
        {
            var session = new SkillCooldownSession();
            session.ApplyFrame(A, 11403, Frame(11403, 0), 1);
            session.OnTurnStart(A);
            Assert.False(session.TryGetRemaining(B, 30502, out _));
        }

        [Fact]
        public void SeededZero_ShowsReadyInsteadOfHidingThePlayer()
        {
            var session = new SkillCooldownSession();
            // 房间快照为空 = 当时就绪: 补 0 之后该玩家也要能显示(否则"4 个人只看到 3 个")。
            session.ApplyFrame(B, 30502, Frame(30502, 0));
            Assert.True(session.TryGetRemaining(B, 30502, out int remaining));
            Assert.Equal(0, remaining);
        }

        [Fact]
        public void CooldownCorrection_IsAbsoluteAndAppliesToTheTarget()
        {
            var session = new SkillCooldownSession();
            session.ApplyFrame(B, 30502, Frame(30502, 2));
            session.ApplyCorrection(B, 1);
            Assert.Equal(1, Remaining(session, B, 30502));
            // 别人的修正不会落到 B 身上。
            session.ApplyCorrection(C, 0);
            Assert.Equal(1, Remaining(session, B, 30502));
        }

        [Fact]
        public void CooldownReduction_LowersRemainingButKeepsTheCap()
        {
            // 角色机制减少的是"当前冷却"(如 109 击倒怪物 -2 / 112 受伤 -1 / 128 经过卡牌 -2),
            // 服务器用 UpdateHeroAttrS2C.Cd 下发绝对值; 上限不受影响。
            var session = new SkillCooldownSession();
            session.ApplyCast(B, 30502, 3);
            session.ApplyCorrection(B, 1);
            Assert.Equal(1, Remaining(session, B, 30502));
            Assert.True(session.TryGetCap(B, 30502, out int cap));
            Assert.Equal(3, cap);
            // 减到 0 就显示就绪, 上限仍然是 3。
            session.ApplyCorrection(B, 0);
            Assert.Equal(0, Remaining(session, B, 30502));
            Assert.True(session.TryGetCap(B, 30502, out cap));
            Assert.Equal(3, cap);
        }

        [Fact]
        public void CooldownReduction_DoesNotDoubleCountTheNextTurn()
        {
            // 减 CD 之后, 下一次"轮到 B"仍然只 -1(不会再额外扣一次)。
            var session = new SkillCooldownSession();
            session.ApplyFrame(A, 11403, Frame(11403, 0), 2);
            session.ApplyCast(B, 30502, 3);
            session.ApplyCorrection(B, 1);
            session.OnTurnStart(B);                 // 这一轮的回合
            Assert.Equal(0, Remaining(session, B, 30502));
            session.ApplyCorrection(B, 2);          // 又减了一次(绝对值)
            Assert.Equal(2, Remaining(session, B, 30502));
            session.ApplyFrame(A, 11403, Frame(11403, 0), 3);
            session.OnTurnStart(B);                 // 下一轮的回合
            Assert.Equal(1, Remaining(session, B, 30502));
        }

        [Fact]
        public void UnknownPlayerId_FallsBackToTheUniqueSkillKey()
        {
            var session = new SkillCooldownSession();
            // 归属者 ID 与游戏内玩家 ID 口径不一致时, 该技能 ID 在本局唯一, 仍能显示。
            session.ApplyFrame(999999, 11403, Frame(11403, 2));
            Assert.True(session.TryGetRemaining(A, 11403, out int remaining));
            Assert.Equal(2, remaining);
        }

        [Fact]
        public void SharedSkillId_DoesNotFallBack()
        {
            var session = new SkillCooldownSession();
            session.SetSharedSkills(new[] { 11403 });
            session.ApplyFrame(999999, 11403, Frame(11403, 2));
            // 两名玩家共用同一技能时按技能键兜底会串号, 宁可不显示。
            Assert.False(session.TryGetRemaining(A, 11403, out _));
        }

        [Fact]
        public void FinalRoundSentinel_IsNotUsedAsCap()
        {
            var session = new SkillCooldownSession();
            // 决赛轮把 98 当上限推下来时不记录, 分母继续用配表值。
            session.SetPlayerSkill(A, 11403);
            session.ApplyCast(A, 11403, 98);
            Assert.False(session.TryGetCap(A, 11403, out _));
        }

        [Fact]
        public void Reset_DropsEverythingForTheNextBattle()
        {
            var session = new SkillCooldownSession();
            session.ApplyFrame(A, 11403, Frame(11403, 2));
            session.SetPlayerSkill(A, 11403);
            session.ApplyCast(A, 11403, 3);
            session.Reset();
            Assert.Equal(0, session.Count);
            Assert.False(session.TryGetRemaining(A, 11403, out _));
            Assert.False(session.TryGetCap(A, 11403, out _));
        }

        private static int Remaining(SkillCooldownSession session, long playerId, int skillId)
        {
            Assert.True(session.TryGetRemaining(playerId, skillId, out int remaining), "玩家 " + playerId + " 没有冷却记录");
            return remaining;
        }
    }
}
