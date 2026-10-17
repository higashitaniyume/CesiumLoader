using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 技能冷却会话（生产代码 SkillCooldownTracker 内部使用的那一层）的离线测试。
    /// 数值只来自服务器：查询回包给剩余冷却，释放技能帧给本次生效上限。
    /// </summary>
    public class SkillCooldownSessionTests
    {
        private const long A = 1184313;
        private const long B = 535688;
        private const long C = 2185070;

        [Fact]
        public void Query_SetsTheRemainingCooldown()
        {
            var session = new SkillCooldownSession();
            session.ApplyQuery(A, 2);
            Assert.True(session.TryGetRemaining(A, out int remaining));
            Assert.Equal(2, remaining);
            Assert.Equal(1, session.Count);
        }

        [Fact]
        public void Query_UnknownPlayer_HasNoValue()
        {
            var session = new SkillCooldownSession();
            session.ApplyQuery(A, 2);
            Assert.False(session.TryGetRemaining(B, out _));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(31)]
        [InlineData(98)]
        public void Query_ImplausibleValue_IsIgnored(int value)
        {
            // 决赛轮的 98 是"结束标记", 不是冷却回合; 负数更是脏数据。
            var session = new SkillCooldownSession();
            session.ApplyQuery(A, value);
            Assert.False(session.TryGetRemaining(A, out _));
        }

        [Fact]
        public void Query_Zero_ShowsReadyInsteadOfHidingThePlayer()
        {
            var session = new SkillCooldownSession();
            session.ApplyQuery(B, 0);
            Assert.True(session.TryGetRemaining(B, out int remaining));
            Assert.Equal(0, remaining);
        }

        [Fact]
        public void Cast_SetsRemainingAndTheEffectiveCap()
        {
            var session = new SkillCooldownSession();
            // 回放：玩家 1184313 释放 11403，applied=3，配表 Round 也是 3。
            session.ApplyCast(A, 11403, 3);
            Assert.True(session.TryGetRemaining(A, out int remaining));
            Assert.Equal(3, remaining);
            Assert.True(session.TryGetCap(A, 11403, out int cap));
            Assert.Equal(3, cap);
        }

        [Fact]
        public void Cast_CapCanDifferFromTheConfigValue()
        {
            var session = new SkillCooldownSession();
            // 回放：玩家 535688 释放 30502，applied=2，而配表 Round=3。
            session.ApplyCast(B, 30502, 2);
            Assert.True(session.TryGetCap(B, 30502, out int cap));
            Assert.Equal(2, cap);
        }

        [Fact]
        public void Query_DoesNotInventACap()
        {
            var session = new SkillCooldownSession();
            session.ApplyQuery(A, 2);
            Assert.False(session.TryGetCap(A, 11403, out _));
        }

        [Fact]
        public void Query_ReductionLowersRemainingButKeepsTheCap()
        {
            // 角色机制减少的是"当前冷却"(109 击倒 -2 / 112 受伤 -1 / 128 经过卡牌 -2)。
            var session = new SkillCooldownSession();
            session.ApplyCast(B, 30502, 3);
            session.ApplyQuery(B, 1);
            Assert.True(session.TryGetRemaining(B, out int remaining));
            Assert.Equal(1, remaining);
            Assert.True(session.TryGetCap(B, 30502, out int cap));
            Assert.Equal(3, cap);
            // 减到 0 就是就绪，上限仍然是 3。
            session.ApplyQuery(B, 0);
            Assert.True(session.TryGetRemaining(B, out remaining));
            Assert.Equal(0, remaining);
            Assert.True(session.TryGetCap(B, 30502, out cap));
            Assert.Equal(3, cap);
        }

        [Fact]
        public void Cap_IsScopedPerSkill_AfterASkillSwitch()
        {
            var session = new SkillCooldownSession();
            session.ApplyCast(A, 11403, 3);
            session.ApplyCast(A, 30502, 2);
            Assert.True(session.TryGetCap(A, 30502, out int newCap));
            Assert.Equal(2, newCap);
            Assert.True(session.TryGetCap(A, 11403, out int oldCap));
            Assert.Equal(3, oldCap);
            // 从没释放过的技能不回退到别的技能的上限，由调用方用配表值。
            Assert.False(session.TryGetCap(A, 99999, out _));
        }

        [Fact]
        public void Cap_FallsBackToThePlayerWhenSkillIsUnknown()
        {
            var session = new SkillCooldownSession();
            session.ApplyCast(A, 11403, 3);
            Assert.True(session.TryGetCap(A, 0, out int cap));
            Assert.Equal(3, cap);
        }

        [Fact]
        public void Cast_SentinelValue_IsNotUsedAsCap()
        {
            var session = new SkillCooldownSession();
            session.ApplyCast(A, 11403, 98);
            Assert.False(session.TryGetCap(A, 11403, out _));
        }

        [Fact]
        public void Reset_DropsEverythingForTheNextBattle()
        {
            var session = new SkillCooldownSession();
            session.ApplyQuery(A, 2);
            session.ApplyCast(A, 11403, 3);
            session.Reset();
            Assert.Equal(0, session.Count);
            Assert.False(session.TryGetRemaining(A, out _));
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
    }
}
