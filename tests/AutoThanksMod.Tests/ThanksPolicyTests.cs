using AutoThanksMod;
using party.model;
using Xunit;

namespace AutoThanksMod.Tests
{
    public class ThanksPolicyTests
    {
        private const long Now = 100 * ThanksPolicy.TicksPerSecond;
        private readonly ThanksPolicy _policy = new ThanksPolicy();

        private bool Accept(out int chatId, long sender = 2, long recipient = 1, long account = 1,
            int type = 3, bool sameTeam = true, bool blocked = false, bool healing = true, bool cards = true, bool transfers = true,
            long now = Now, long received = Now, int cooldown = 2, int delay = 0)
        {
            return _policy.TryGetChatId(sender, recipient, account, type, sameTeam, blocked, healing, cards, transfers,
                received, now, cooldown, out chatId, delay);
        }

        [Theory]
        [InlineData(TriggerPhrase.Types.Type.CureFriend, 50001)]
        [InlineData(TriggerPhrase.Types.Type.GiveCard, 50002)]
        [InlineData(TriggerPhrase.Types.Type.TransferGold, 50000)]
        public void ServerEventUsesTheOriginalQuickReply(TriggerPhrase.Types.Type type, int expected)
        {
            Assert.True(Accept(out int id, type: (int)type));
            Assert.Equal(expected, id);
        }

        [Theory]
        [InlineData(TriggerPhrase.Types.Type.None)]
        [InlineData(TriggerPhrase.Types.Type.KillBoss)]
        [InlineData((TriggerPhrase.Types.Type)99)]
        public void OtherEventsNeverThank(TriggerPhrase.Types.Type type)
        {
            Assert.False(Accept(out int id, type: (int)type));
            Assert.Equal(0, id);
        }

        [Theory]
        [InlineData(0, 1, 1)]
        [InlineData(1, 1, 1)]
        [InlineData(2, 3, 1)]
        [InlineData(2, 1, 0)]
        public void OnlyHelpFromAnotherPlayerToTheActualAccountCounts(long sender, long recipient, long account)
        {
            Assert.False(Accept(out _, sender: sender, recipient: recipient, account: account));
        }

        [Fact]
        public void EnemyOrBlockedSenderIsIgnored()
        {
            Assert.False(Accept(out _, sameTeam: false));
            Assert.False(Accept(out _, blocked: true));
        }

        [Fact]
        public void HealingAndCardsHaveIndependentSwitches()
        {
            Assert.False(Accept(out _, healing: false));
            Assert.True(Accept(out _, type: 2, healing: false));
            Assert.False(Accept(out _, type: 2, cards: false));
            Assert.True(Accept(out _, cards: false));
        }

        [Fact]
        public void CooldownCoalescesAllKindsOfHelpAndRepeatedNotifications()
        {
            Assert.True(Accept(out _));
            _policy.MarkAttempt(Now);
            Assert.False(Accept(out _, now: Now + ThanksPolicy.TicksPerSecond));
            Assert.False(Accept(out _, type: 2, now: Now + ThanksPolicy.TicksPerSecond));
            Assert.False(Accept(out _, type: 1, now: Now + ThanksPolicy.TicksPerSecond));
            Assert.True(Accept(out _, type: 2, now: Now + 2 * ThanksPolicy.TicksPerSecond));
        }

        [Fact]
        public void StaleAndFutureNotificationsAreDiscarded()
        {
            Assert.False(Accept(out _, received: Now - 6 * ThanksPolicy.TicksPerSecond));
            Assert.False(Accept(out _, received: Now + 1));
        }

        [Fact]
        public void ResetAllowsTheFirstThankInANewBattle()
        {
            _policy.MarkAttempt(Now);
            Assert.False(Accept(out _));
            _policy.Reset();
            Assert.True(Accept(out _));
        }

        [Fact]
        public void ZeroCooldownIsSupportedAndInvalidLargeValuesAreClamped()
        {
            _policy.MarkAttempt(Now);
            Assert.True(Accept(out _, cooldown: 0));
            Assert.False(Accept(out _, cooldown: 999, now: Now + 29 * ThanksPolicy.TicksPerSecond,
                received: Now + 29 * ThanksPolicy.TicksPerSecond));
            Assert.True(Accept(out _, cooldown: 999, now: Now + 30 * ThanksPolicy.TicksPerSecond,
                received: Now + 30 * ThanksPolicy.TicksPerSecond));
        }

        [Fact]
        public void TransfersHaveAnIndependentSwitch()
        {
            Assert.False(Accept(out _, type: 1, transfers: false));
            Assert.True(Accept(out int id, type: 1, healing: false, cards: false));
            Assert.Equal(50000, id);
            Assert.True(Accept(out _, type: 3, transfers: false));
            Assert.True(Accept(out _, type: 2, transfers: false));
        }

        [Fact]
        public void TransfersOnlyThankUnblockedTeammatesHelpingTheActualAccount()
        {
            Assert.False(Accept(out _, type: 1, sender: 1));
            Assert.False(Accept(out _, type: 1, recipient: 3));
            Assert.False(Accept(out _, type: 1, sameTeam: false));
            Assert.False(Accept(out _, type: 1, blocked: true));
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(0, 0)]
        [InlineData(1000, 1000)]
        [InlineData(3000, 3000)]
        [InlineData(3001, 3000)]
        [InlineData(int.MaxValue, 3000)]
        public void DelayConfigurationIsClamped(int input, int expected)
        {
            Assert.Equal(expected, ThanksPolicy.ClampDelayMilliseconds(input));
            Assert.Equal(1000, ThanksPolicy.DefaultDelayMilliseconds);
        }

        [Fact]
        public void QueueDelayCheckUsesTheSameBoundaryWithoutExtendingEventValidity()
        {
            Assert.False(ThanksPolicy.IsDelayElapsed(Now, Now - 1, 0));
            Assert.True(ThanksPolicy.IsDelayElapsed(Now, Now, 0));
            Assert.True(ThanksPolicy.IsDelayElapsed(Now, Now, int.MinValue));
            Assert.False(ThanksPolicy.IsDelayElapsed(Now, Now + 9999999, 1000));
            Assert.True(ThanksPolicy.IsDelayElapsed(Now, Now + 10000000, 1000));
            Assert.False(ThanksPolicy.IsDelayElapsed(Now, Now + 29999999, int.MaxValue));
            Assert.True(ThanksPolicy.IsDelayElapsed(Now, Now + 30000000, int.MaxValue));
            Assert.True(ThanksPolicy.IsDelayElapsed(Now, Now + 6 * ThanksPolicy.TicksPerSecond, 3000));
            Assert.False(Accept(out _, now: Now + 6 * ThanksPolicy.TicksPerSecond, delay: 3000));
        }

        [Fact]
        public void LegacyCallWithoutDelayStillThanksImmediately()
        {
            Assert.True(_policy.TryGetChatId(2, 1, 1, 3, true, false, true, true, true,
                Now, Now, 2, out int id));
            Assert.Equal(50001, id);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(1000)]
        [InlineData(3000)]
        [InlineData(int.MaxValue)]
        public void NotificationMustReachTheExactDelayBoundary(int delay)
        {
            long boundary = Now + ThanksPolicy.ClampDelayMilliseconds(delay) * 10000L;
            Assert.False(Accept(out int id, now: boundary - 1, delay: delay));
            Assert.Equal(0, id);
            Assert.True(Accept(out id, now: boundary, delay: delay));
            Assert.Equal(50001, id);
            Assert.True(Accept(out _, now: boundary + 1, delay: delay));
        }

        [Fact]
        public void ZeroAndNegativeDelaysAreImmediateButFutureNotificationsRemainInvalid()
        {
            Assert.True(Accept(out _, delay: 0));
            Assert.True(Accept(out _, delay: int.MinValue));
            Assert.False(Accept(out _, delay: int.MinValue, received: Now + 1));
        }

        [Theory]
        [InlineData(1000, 2, 20000000)]
        [InlineData(3000, 1, 30000000)]
        [InlineData(3000, 0, 30000000)]
        public void DelayAndCooldownMustBothExpire(int delay, int cooldown, long elapsed)
        {
            _policy.MarkAttempt(Now);
            Assert.False(Accept(out _, now: Now + elapsed - 1, delay: delay, cooldown: cooldown));
            Assert.True(Accept(out _, now: Now + elapsed, delay: delay, cooldown: cooldown));
        }

        [Fact]
        public void DelayStartsAtNotificationReceiptRatherThanPreviousAttempt()
        {
            _policy.MarkAttempt(Now - 10 * ThanksPolicy.TicksPerSecond);
            Assert.False(Accept(out _, now: Now + 9999999, delay: 1000));
            Assert.True(Accept(out _, now: Now + ThanksPolicy.TicksPerSecond, delay: 1000));
        }

        [Fact]
        public void MaximumDelayDoesNotExtendNotificationLifetime()
        {
            Assert.True(Accept(out _, now: Now + 3 * ThanksPolicy.TicksPerSecond, delay: 3000));
            Assert.True(Accept(out _, now: Now + 5 * ThanksPolicy.TicksPerSecond, delay: 3000));
            Assert.False(Accept(out int id, now: Now + 5 * ThanksPolicy.TicksPerSecond + 1, delay: 3000));
            Assert.Equal(0, id);
        }

        [Fact]
        public void ResetClearsCooldownButStillHonorsNotificationDelay()
        {
            _policy.MarkAttempt(Now);
            _policy.Reset();
            Assert.False(Accept(out _, now: Now + 9999999, delay: 1000, cooldown: 30));
            Assert.True(Accept(out _, now: Now + ThanksPolicy.TicksPerSecond, delay: 1000, cooldown: 30));
        }

        [Fact]
        public void WaitingForDelayDoesNotReserveAnAttempt()
        {
            Assert.False(Accept(out _, now: Now + 9999999, delay: 1000));
            Assert.True(Accept(out _, now: Now + ThanksPolicy.TicksPerSecond, delay: 1000));
            Assert.True(Accept(out _, type: 2, now: Now + ThanksPolicy.TicksPerSecond, delay: 1000));
        }

        [Fact]
        public void IgnoredEventsDoNotConsumeCooldown()
        {
            Assert.False(Accept(out _, type: 0));
            Assert.True(Accept(out _));
        }
    }
}
