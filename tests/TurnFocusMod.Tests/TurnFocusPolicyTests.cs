using System;
using TurnFocusMod;
using Xunit;

namespace TurnFocusMod.Tests
{
    public class TurnFocusPolicyTests
    {
        private const long Second = TimeSpan.TicksPerSecond;

        [Fact]
        public void IsFocusAction_MatchesOnlyInteractiveWindows()
        {
            Assert.True(TurnFocusPolicy.IsFocusAction(5211)); // 筹码三选一
            Assert.True(TurnFocusPolicy.IsFocusAction(5047)); // 战斗询问
            Assert.True(TurnFocusPolicy.IsFocusAction(5035)); // 战斗出牌
            Assert.True(TurnFocusPolicy.IsFocusAction(5037)); // 攻击骰
            Assert.True(TurnFocusPolicy.IsFocusAction(5039)); // 防御选择

            Assert.False(TurnFocusPolicy.IsFocusAction(5027)); // 普通移动
            Assert.False(TurnFocusPolicy.IsFocusAction(5029)); // 商店
            Assert.False(TurnFocusPolicy.IsFocusAction(0));
        }

        [Fact]
        public void ActionName_CoversEveryFocusAction()
        {
            Assert.Equal("选择筹码", TurnFocusPolicy.ActionName(5211));
            Assert.Equal("战斗询问", TurnFocusPolicy.ActionName(5047));
            Assert.Equal("战斗出牌", TurnFocusPolicy.ActionName(5035));
            Assert.Equal("攻击骰", TurnFocusPolicy.ActionName(5037));
            Assert.Equal("防御选择", TurnFocusPolicy.ActionName(5039));
            Assert.Equal("操作", TurnFocusPolicy.ActionName(9999));
        }

        [Fact]
        public void IsMyTurn_OnlyMatchesPositiveEqualIds()
        {
            Assert.True(TurnFocusPolicy.IsMyTurn(42, 42));
            Assert.False(TurnFocusPolicy.IsMyTurn(42, 43));
            Assert.False(TurnFocusPolicy.IsMyTurn(0, 0));
            Assert.False(TurnFocusPolicy.IsMyTurn(-1, -1));
        }

        [Fact]
        public void ShouldRequest_RequiresEveryCondition()
        {
            long now = 10 * Second;
            Assert.True(TurnFocusPolicy.ShouldRequest(7, 7, true, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequest(7, 7, false, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequest(7, 7, true, true, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequest(7, 7, true, false, false, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequest(7, 8, true, false, true, now, 0, 3));
        }

        [Fact]
        public void Cooldown_BlocksUntilElapsed()
        {
            long last = 10 * Second;
            Assert.False(TurnFocusPolicy.CooldownElapsed(last + 1, last, 3));
            Assert.False(TurnFocusPolicy.CooldownElapsed(last + 2 * Second, last, 3));
            Assert.True(TurnFocusPolicy.CooldownElapsed(last + 3 * Second, last, 3));
            Assert.True(TurnFocusPolicy.CooldownElapsed(last, last, 0));
            Assert.True(TurnFocusPolicy.CooldownElapsed(last, 0, 3));
        }

        [Fact]
        public void ClampCooldown_HandlesDirtyConfig()
        {
            Assert.Equal(0, TurnFocusPolicy.ClampCooldownSeconds(-5));
            Assert.Equal(3, TurnFocusPolicy.ClampCooldownSeconds(3));
            Assert.Equal(60, TurnFocusPolicy.ClampCooldownSeconds(999));
        }

        [Fact]
        public void ShouldRequest_AllowsNextTurnAfterCooldown()
        {
            long last = 10 * Second;
            Assert.False(TurnFocusPolicy.ShouldRequest(7, 7, true, false, true, last + 1, last, 3));
            Assert.True(TurnFocusPolicy.ShouldRequest(7, 7, true, false, true, last + 3 * Second, last, 3));
        }

        [Fact]
        public void IsOtherPlayer_OnlyMatchesDifferentPositiveIds()
        {
            Assert.True(TurnFocusPolicy.IsOtherPlayer(42, 43));
            Assert.False(TurnFocusPolicy.IsOtherPlayer(42, 42));
            Assert.False(TurnFocusPolicy.IsOtherPlayer(0, 43));
            Assert.False(TurnFocusPolicy.IsOtherPlayer(42, 0));
            Assert.False(TurnFocusPolicy.IsOtherPlayer(-1, 42));
        }

        [Fact]
        public void ShouldRequestRoomChat_RequiresEveryCondition()
        {
            long now = 10 * Second;
            Assert.True(TurnFocusPolicy.ShouldRequestRoomChat(true, true, true, true, false, true, now, 0, 3));

            Assert.False(TurnFocusPolicy.ShouldRequestRoomChat(false, true, true, true, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestRoomChat(true, false, true, true, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestRoomChat(true, true, false, true, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestRoomChat(true, true, true, false, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestRoomChat(true, true, true, true, true, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestRoomChat(true, true, true, true, false, false, now, 0, 3));
        }

        [Fact]
        public void ShouldRequestRoomChat_RespectsCooldown()
        {
            long last = 10 * Second;
            Assert.False(TurnFocusPolicy.ShouldRequestRoomChat(true, true, true, true, false, true, last + 1, last, 3));
            Assert.True(TurnFocusPolicy.ShouldRequestRoomChat(true, true, true, true, false, true, last + 3 * Second, last, 3));
        }

        [Fact]
        public void ShouldRequestMatchSuccess_RequiresEveryCondition()
        {
            long now = 10 * Second;
            Assert.True(TurnFocusPolicy.ShouldRequestMatchSuccess(true, true, false, true, now, 0, 3));

            Assert.False(TurnFocusPolicy.ShouldRequestMatchSuccess(false, true, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestMatchSuccess(true, false, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestMatchSuccess(true, true, true, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestMatchSuccess(true, true, false, false, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestMatchSuccess(true, true, false, true, now + 1, now, 3));
            Assert.True(TurnFocusPolicy.ShouldRequestMatchSuccess(true, true, false, true, now + 3 * Second, now, 3));
        }

        [Fact]
        public void ShouldRequestRoomInvite_RequiresEveryCondition()
        {
            long now = 10 * Second;
            Assert.True(TurnFocusPolicy.ShouldRequestRoomInvite(true, true, true, false, true, now, 0, 3));

            Assert.False(TurnFocusPolicy.ShouldRequestRoomInvite(false, true, true, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestRoomInvite(true, false, true, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestRoomInvite(true, true, false, false, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestRoomInvite(true, true, true, true, true, now, 0, 3));
            Assert.False(TurnFocusPolicy.ShouldRequestRoomInvite(true, true, true, false, false, now, 0, 3));
        }

        [Fact]
        public void ShouldRequestRoomInvite_RespectsCooldown()
        {
            long last = 10 * Second;
            Assert.False(TurnFocusPolicy.ShouldRequestRoomInvite(true, true, true, false, true, last + 1, last, 3));
            Assert.True(TurnFocusPolicy.ShouldRequestRoomInvite(true, true, true, false, true, last + 3 * Second, last, 3));
        }
    }
}
