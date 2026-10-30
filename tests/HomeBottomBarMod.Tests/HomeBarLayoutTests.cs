using System.Collections.Generic;
using HomeBottomBarMod;
using Xunit;

namespace HomeBottomBarMod.Tests
{
    public class HomeBarLayoutTests
    {
        [Fact]
        public void CurrentHomeLayout_MovesSettingLeftAndAddsExitAtRight()
        {
            var input = new List<int> { 8, 7, 9, 5, 2, 3, 6, 1 };

            var output = HomeBarLayout.Desired(input);

            Assert.Equal(new List<int> { 8, 7, 9, 5, 2, 3, 1, 0 }, output);
            Assert.False(HomeBarLayout.IsDesired(input));
            Assert.True(HomeBarLayout.IsDesired(output));
        }

        [Fact]
        public void DesiredLayout_IsIdempotent()
        {
            var input = new List<int> { 8, 7, 9, 5, 2, 3, 1, 0 };

            Assert.True(HomeBarLayout.IsDesired(input));
            Assert.Equal(input, HomeBarLayout.Desired(input));
        }

        [Fact]
        public void OldExitAndEveryNotice_AreRemoved()
        {
            var input = new List<int> { 0, 8, 6, 7, 0, 1, 9 };

            Assert.Equal(new List<int> { 8, 7, 9, 1, 0 }, HomeBarLayout.Desired(input));
        }

        [Fact]
        public void EmptyLayout_BecomesExitOnly()
        {
            Assert.Equal(new List<int> { 0 }, HomeBarLayout.Desired(new List<int>()));
        }

        [Fact]
        public void MissingSetting_DoesNotInventOne()
        {
            Assert.Equal(new List<int> { 8, 7, 0 }, HomeBarLayout.Desired(new List<int> { 8, 6, 7 }));
        }

        [Fact]
        public void AlreadyDesiredWithExtraFunction_IsNotReportedAsDesired()
        {
            var input = new List<int> { 8, 7, 9, 1, 2, 0 };

            Assert.False(HomeBarLayout.IsDesired(input));
            Assert.Equal(new List<int> { 8, 7, 9, 2, 1, 0 }, HomeBarLayout.Desired(input));
        }
    }
}
