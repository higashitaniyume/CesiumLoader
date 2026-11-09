using Xunit;

namespace TurnClockMod.Tests
{
    public sealed class UnityFrameClockTests
    {
        [Theory]
        [InlineData(.08f, .02f, 4)]
        [InlineData(.1f, .05f, 2)]
        [InlineData(0, .02f, 0)]
        [InlineData(.01f, .02f, .5)]
        public void UsesActualFrameDeltasIncludingPauseAndClamping(float delta, float unscaled, double expected)
        {
            Assert.Equal(expected, UnityFrameClock.Scale(delta, unscaled).Value, 5);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(.02f, -1)]
        [InlineData(-.02f, .02f)]
        [InlineData(float.NaN, .02f)]
        [InlineData(.02f, float.NaN)]
        [InlineData(float.PositiveInfinity, .02f)]
        [InlineData(.02f, float.PositiveInfinity)]
        public void InvalidDeltasRequestFallbackRatherThanPropagateBadRates(float delta, float unscaled)
        {
            Assert.Null(UnityFrameClock.Scale(delta, unscaled));
        }
    }
}
