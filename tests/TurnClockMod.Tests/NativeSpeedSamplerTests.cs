using System;
using Xunit;

namespace TurnClockMod.Tests
{
    public sealed class NativeSpeedSamplerTests
    {
        [Fact]
        public void PersistentAnomaliesCannotBypassReadBudget()
        {
            var sampler = new NativeSpeedSampler();
            int reads = 0;
            Func<double?> read = () => { reads++; return 4; };
            for (int frame = 0; frame <= 1000; frame++)
            {
                sampler.RequestRefresh();
                sampler.Sample(frame * .001, .2, read);
            }
            Assert.InRange(reads, 19, 21);
        }

        [Fact]
        public void NormalPollingHonorsConfiguredIntervalAndCoalescesSameFrameRefreshes()
        {
            var sampler = new NativeSpeedSampler();
            int reads = 0;
            Func<double?> read = () => { reads++; return 1; };
            Assert.Equal(1, sampler.Sample(10, .2, read));
            sampler.RequestRefresh();
            sampler.Sample(10, .2, read);
            sampler.Sample(10.01, .2, read);
            Assert.Equal(1, reads);
            sampler.Sample(10.06, .2, read); Assert.Equal(2, reads);
            sampler.Sample(10.2, .2, read); Assert.Equal(2, reads);
            sampler.Sample(10.27, .2, read); Assert.Equal(3, reads);
        }

        [Fact]
        public void LostAcknowledgementIsUnknownNotOneOrThePreviousSpeed()
        {
            var sampler = new NativeSpeedSampler();
            double? state = 4;
            Assert.Equal(4, sampler.Sample(0, .2, () => state));
            state = null;
            Assert.Null(sampler.Sample(.21, .2, () => state));
            Assert.Null(sampler.Sample(.3, .2, () => 1));
            Assert.Equal(1, sampler.Sample(.42, .2, () => 1));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(0.5)]
        [InlineData(101)]
        public void InvalidAcknowledgementsAreUnknown(double speed)
        {
            Assert.Null(new NativeSpeedSampler().Sample(0, .2, () => speed));
        }

        [Fact]
        public void ReaderFailureAndClockRollbackDoNotRetainStaleSpeed()
        {
            var sampler = new NativeSpeedSampler();
            Assert.Equal(4, sampler.Sample(100, 5, () => 4));
            Assert.Null(sampler.Sample(90, 5, () => throw new InvalidOperationException()));
            sampler.Reset();
            Assert.Equal(1, sampler.Sample(90, 5, () => 1));
        }

        [Theory]
        [InlineData(double.NaN, .2)]
        [InlineData(double.PositiveInfinity, .2)]
        [InlineData(double.NegativeInfinity, .2)]
        [InlineData(-1, .05)]
        [InlineData(0, .05)]
        [InlineData(10, 5)]
        public void RefreshIntervalHasFiniteBounds(double input, double expected)
        {
            Assert.Equal(expected, NativeSpeedSampler.NormalizeInterval(input));
        }
    }
}
