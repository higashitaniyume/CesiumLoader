using ChatDurationMod;
using Xunit;

namespace ChatDurationMod.Tests
{
    public class ChatDurationRulesTests
    {
        [Theory]
        [InlineData(15, 15)]
        [InlineData(1, 1)]
        [InlineData(300, 300)]
        [InlineData(0, 15)]
        [InlineData(-5, 15)]
        [InlineData(301, 300)]
        [InlineData(9999, 300)]
        public void NormalizeSeconds_KeepsRangeAndFallsBackForNonPositive(int input, int expected)
        {
            Assert.Equal(expected, ChatDurationRules.NormalizeSeconds(input));
        }

        [Fact]
        public void IsDue_IsInclusiveAtDuration()
        {
            Assert.False(ChatDurationRules.IsDue(10.0, 24.999, 15));
            Assert.True(ChatDurationRules.IsDue(10.0, 25.0, 15));
        }

        [Fact]
        public void IsDue_NonPositiveDurationIsImmediatelyDue()
        {
            Assert.True(ChatDurationRules.IsDue(10.0, 10.0, 0));
            Assert.True(ChatDurationRules.IsDue(10.0, 10.0, -1));
        }
    }
}
