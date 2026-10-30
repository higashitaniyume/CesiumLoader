using Xunit;

namespace UnlockFrameRateMod.Tests
{
    public class FrameRateOptionsTests
    {
        [Fact]
        public void DefaultOptions_EndWithUnlimited()
        {
            Assert.Equal(new[] { 144, 165, 240, 360, FrameRateOptions.Unlimited }, FrameRateOptions.Values);
        }

        [Fact]
        public void Options_AreValidAndDistinct()
        {
            var seen = new System.Collections.Generic.HashSet<int>();
            foreach (int value in FrameRateOptions.Values)
            {
                Assert.True(FrameRateOptions.IsValidValue(value), "无效帧率: " + value);
                Assert.True(seen.Add(value), "重复帧率: " + value);
            }
        }

        [Theory]
        [InlineData(900000, true)]
        [InlineData(900004, true)]
        [InlineData(909999, true)]
        [InlineData(1204, false)]
        [InlineData(899999, false)]
        [InlineData(910000, false)]
        public void IsExtraDescriptionId_UsesReservedRange(int id, bool expected)
        {
            Assert.Equal(expected, FrameRateOptions.IsExtraDescriptionId(id));
        }

        [Fact]
        public void Labels_MatchGameFormat()
        {
            Assert.Equal("144帧", FrameRateOptions.Simplified(144));
            Assert.Equal("144幀", FrameRateOptions.Traditional(144));
            Assert.Equal("144 FPS", FrameRateOptions.English(144));
            Assert.Equal("不限帧", FrameRateOptions.Simplified(FrameRateOptions.Unlimited));
            Assert.Equal("不限幀", FrameRateOptions.Traditional(FrameRateOptions.Unlimited));
            Assert.Equal("Unlimited", FrameRateOptions.English(FrameRateOptions.Unlimited));
            Assert.Equal("無制限", FrameRateOptions.Japanese(FrameRateOptions.Unlimited));
            Assert.Equal("무제한", FrameRateOptions.Korean(FrameRateOptions.Unlimited));
        }

        [Fact]
        public void Describe_IsReadable()
        {
            Assert.Contains("不限", FrameRateOptions.Describe(FrameRateOptions.Unlimited));
            Assert.Contains("144", FrameRateOptions.Describe(144));
        }
    }
}
