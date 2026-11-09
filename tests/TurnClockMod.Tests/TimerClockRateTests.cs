using System;
using Xunit;

namespace TurnClockMod.Tests
{
    public sealed class TimerClockRateTests
    {
        private sealed class Timer
        {
            public bool usesRealTime { get; set; }
            public float Elapsed = 0, Start = 0;
        }
        private sealed class FieldTimer { public bool usesRealTime = false; }
        private sealed class UnknownTimer { }
        private sealed class BrokenTimer { public bool usesRealTime => throw new InvalidOperationException(); }

        [Theory]
        [InlineData(false, 1, 4, 4)]
        [InlineData(true, 1, 4, 1)]
        [InlineData(false, 3, 1, 3)]
        [InlineData(true, 3, 4, 3)]
        [InlineData(false, 2, 3, 6)]
        [InlineData(false, 1, 0, 1)]
        [InlineData(false, 1, 0.5, 1)]
        public void SelectsClockPerInstance(bool realtime, double native, double scale, double expected)
        {
            var policy = new TimerClockRate(typeof(Timer));
            var timer = new Timer { usesRealTime = realtime };
            Assert.Equal(expected, policy.EffectiveSpeed(timer, native, scale));
        }

        [Fact]
        public void SharedMetadataDoesNotCacheAnInstancesClockChoice()
        {
            var policy = new TimerClockRate(typeof(Timer));
            Assert.Equal(4, policy.EffectiveSpeed(new Timer(), 1, 4));
            Assert.Equal(1, policy.EffectiveSpeed(new Timer { usesRealTime = true }, 1, 4));
            Assert.Equal(4, new TimerClockRate(typeof(FieldTimer)).EffectiveSpeed(new FieldTimer(), 1, 4));
        }

        [Fact]
        public void UnknownBrokenOrInvalidClockDoesNotGuessScaledTime()
        {
            Assert.Equal(2, new TimerClockRate(typeof(UnknownTimer)).EffectiveSpeed(new UnknownTimer(), 2, 4));
            Assert.Equal(2, new TimerClockRate(typeof(BrokenTimer)).EffectiveSpeed(new BrokenTimer(), 2, 4));
            Assert.Equal(2, new TimerClockRate(typeof(Timer)).EffectiveSpeed(new Timer(), 2, double.NaN));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void UnityAcceleration_KeepsBothTimerModesAtWallClockRate(bool realtime, bool startMode)
        {
            var timer = new Timer { usesRealTime = realtime };
            var policy = new TimerClockRate(typeof(Timer));
            var compensator = new TimerCompensator();
            var member = TimerMember.ForField(typeof(Timer).GetField(startMode ? "Start" : "Elapsed"));
            float clock = 0;
            for (int frame = 0; frame <= 100; frame++)
            {
                if (frame > 0)
                {
                    float delta = realtime ? .02f : .08f;
                    clock += delta;
                    timer.Elapsed += delta;
                }
                double speed = policy.EffectiveSpeed(timer, 1, 4);
                if (startMode) compensator.TickStart(timer, clock, timer.Start, speed, member);
                else compensator.TickElapsed(timer, timer.Elapsed, 30, speed, member);
            }
            // Start-mode intentionally retains its minimum 50ms on the first tick.
            Assert.InRange(startMode ? clock - timer.Start : timer.Elapsed, 1.999f, 2.031f);
        }
    }
}
