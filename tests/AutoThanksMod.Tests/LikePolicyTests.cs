using AutoThanksMod;
using Xunit;

namespace AutoThanksMod.Tests
{
    public class LikePolicyTests
    {
        private const long Now = 100 * ThanksPolicy.TicksPerSecond;
        private readonly LikePolicy _policy = new LikePolicy();

        [Theory]
        [InlineData(int.MinValue, 100)]
        [InlineData(0, 100)]
        [InlineData(99, 100)]
        [InlineData(100, 100)]
        [InlineData(500, 500)]
        [InlineData(30000, 30000)]
        [InlineData(30001, 30000)]
        [InlineData(int.MaxValue, 30000)]
        public void IntervalIsClampedToSupportedBounds(int input, int expected)
        {
            Assert.Equal(expected, LikePolicy.ClampIntervalMilliseconds(input));
            Assert.Equal(500, LikePolicy.DefaultIntervalMilliseconds);
        }

        [Fact]
        public void FirstPlayerCanBeAttemptedImmediatelyEvenAtZeroTicks()
        {
            Assert.True(_policy.CanAttempt(1, 0));
            Assert.False(_policy.CanAttempt(0, 0));
            Assert.False(_policy.HasAttempted(1));
        }

        [Fact]
        public void CheckingEligibilityDoesNotConsumeThePlayerOrInterval()
        {
            Assert.True(_policy.CanAttempt(1, Now));
            Assert.True(_policy.CanAttempt(1, Now));
            Assert.True(_policy.CanAttempt(2, Now));
            Assert.False(_policy.HasAttempted(1));
        }

        [Fact]
        public void DefaultIntervalIsBetweenDifferentPlayersAtTheExactTickBoundary()
        {
            _policy.MarkAttempt(1, Now);
            Assert.False(_policy.CanAttempt(2, Now));
            Assert.False(_policy.CanAttempt(2, Now + 500 * 10000L - 1));
            Assert.True(_policy.CanAttempt(2, Now + 500 * 10000L));
            Assert.True(_policy.CanAttempt(2, Now + 500 * 10000L + 1));
        }

        [Theory]
        [InlineData(0, 100)]
        [InlineData(100, 100)]
        [InlineData(500, 500)]
        [InlineData(30000, 30000)]
        [InlineData(int.MaxValue, 30000)]
        public void ConfiguredIntervalUsesRealTicksAndClampedBounds(int interval, int expected)
        {
            _policy.MarkAttempt(1, Now);
            Assert.False(_policy.CanAttempt(2, Now + expected * 10000L - 1, interval));
            Assert.True(_policy.CanAttempt(2, Now + expected * 10000L, interval));
        }

        [Fact]
        public void EachPlayerIsAttemptedOnlyOnceRegardlessOfSuccessOrFailure()
        {
            // Mark before the caller invokes RPC: neither an exception nor a failed response clears it.
            _policy.MarkAttempt(1, Now);
            Assert.True(_policy.HasAttempted(1));
            Assert.False(_policy.CanAttempt(1, Now + 60 * ThanksPolicy.TicksPerSecond));
            Assert.True(_policy.CanAttempt(2, Now + 60 * ThanksPolicy.TicksPerSecond));
            _policy.MarkAttempt(2, Now + 60 * ThanksPolicy.TicksPerSecond);
            Assert.True(_policy.HasAttempted(2));
            Assert.False(_policy.CanAttempt(2, Now + 120 * ThanksPolicy.TicksPerSecond));
            Assert.False(_policy.CanAttempt(1, Now + 120 * ThanksPolicy.TicksPerSecond));
        }

        [Fact]
        public void ALongStallAllowsOneAttemptWithoutCatchUpSendsInTheSameFrame()
        {
            _policy.MarkAttempt(1, Now);
            long afterStall = Now + 60 * ThanksPolicy.TicksPerSecond;
            Assert.True(_policy.CanAttempt(2, afterStall));
            _policy.MarkAttempt(2, afterStall);
            Assert.False(_policy.CanAttempt(2, afterStall));
            Assert.False(_policy.CanAttempt(3, afterStall));
            Assert.False(_policy.CanAttempt(4, afterStall));
            Assert.False(_policy.CanAttempt(3, afterStall + 500 * 10000L - 1));
            Assert.True(_policy.CanAttempt(3, afterStall + 500 * 10000L));
            _policy.MarkAttempt(3, afterStall + 500 * 10000L);
            Assert.False(_policy.CanAttempt(4, afterStall + 500 * 10000L));
        }

        [Fact]
        public void ABackwardsClockDoesNotMakeAnotherPlayerEligible()
        {
            _policy.MarkAttempt(1, Now);
            Assert.False(_policy.CanAttempt(2, Now - 1));
            Assert.False(_policy.CanAttempt(2, Now));
        }

        [Fact]
        public void ResetClearsBothPerPlayerDeduplicationAndGlobalInterval()
        {
            _policy.MarkAttempt(1, Now);
            Assert.False(_policy.CanAttempt(1, Now));
            Assert.False(_policy.CanAttempt(2, Now));
            _policy.Reset();
            Assert.False(_policy.HasAttempted(1));
            Assert.True(_policy.CanAttempt(1, 0));
            Assert.True(_policy.CanAttempt(2, 0));
            _policy.MarkAttempt(1, 0);
            Assert.True(_policy.HasAttempted(1));
            Assert.False(_policy.CanAttempt(1, 500 * 10000L));
            Assert.False(_policy.CanAttempt(2, 500 * 10000L - 1));
            Assert.True(_policy.CanAttempt(2, 500 * 10000L));
        }

        [Fact]
        public void ManualAttemptQueryAvoidsRefreshingTheIntervalEveryFrame()
        {
            if (!_policy.HasAttempted(1)) _policy.MarkAttempt(1, Now);
            long nextFrame = Now + 499 * 10000L;
            if (!_policy.HasAttempted(1)) _policy.MarkAttempt(1, nextFrame);
            Assert.True(_policy.CanAttempt(2, Now + 500 * 10000L));
        }
    }
}
