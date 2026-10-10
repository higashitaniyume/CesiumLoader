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
            long now = Now, long received = Now, int cooldown = 2)
        {
            return _policy.TryGetChatId(sender, recipient, account, type, sameTeam, blocked, healing, cards, transfers,
                received, now, cooldown, out chatId);
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

        [Fact]
        public void IgnoredEventsDoNotConsumeCooldown()
        {
            Assert.False(Accept(out _, type: 0));
            Assert.True(Accept(out _));
        }
    }
}
