using System;
using System.Reflection;
using CombatOddsMod;
using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// BattleOddsFeed 是静态「信箱」，同一进程里状态会累积，所以每个用例先把它复位。
    /// 复位用反射改私有字段：不想为了测试在 mod 里加测试专用 API。
    /// </summary>
    public class BattleOddsFeedTests
    {
        private static readonly FieldInfo VersionField = typeof(BattleOddsFeed).GetField("_version", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo ClaimTicksField = typeof(BattleOddsFeed).GetField("_claimTicks", BindingFlags.NonPublic | BindingFlags.Static);

        private static void Reset()
        {
            VersionField.SetValue(null, 0L);
            ClaimTicksField.SetValue(null, 0L);
            BattleOddsFeed.Clear();
        }

        [Fact]
        public void NobodyClaimedYetIsNotExternal()
        {
            Reset();
            Assert.False(BattleOddsFeed.ExternallyDisplayed);
        }

        [Fact]
        public void ClaimMarksExternalOnlyWhileWithinTheWindow()
        {
            Reset();
            BattleOddsFeed.ClaimDisplay();
            Assert.True(BattleOddsFeed.ExternallyDisplayed);

            // 声明正好过期（外部面板关掉后的自动恢复边界）
            ClaimTicksField.SetValue(null, DateTime.UtcNow.AddSeconds(-BattleOddsFeed.ClaimSeconds).Ticks);
            Assert.False(BattleOddsFeed.ExternallyDisplayed);
        }

        [Fact]
        public void PublishIncrementsVersionAndReplacesLatest()
        {
            Reset();
            var first = new BattleOddsSnapshot { BattleId = 1 };
            var second = new BattleOddsSnapshot { BattleId = 2 };
            BattleOddsFeed.Publish(first);
            Assert.Equal(1, first.Version);
            Assert.Same(first, BattleOddsFeed.Latest);

            BattleOddsFeed.Publish(second);
            Assert.Equal(2, second.Version);
            Assert.Same(second, BattleOddsFeed.Latest);

            BattleOddsFeed.Clear();
            Assert.Null(BattleOddsFeed.Latest);
        }
    }
}
