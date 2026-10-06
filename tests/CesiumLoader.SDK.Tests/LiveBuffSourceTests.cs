using System.Collections.Generic;
using System.Linq;
using CesiumLoader.SDK.Gameplay;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    public class LiveBuffSourceTests
    {
        [Fact]
        public void ConsumedShield_EmptyLiveContainerDoesNotRestoreSnapshotShield()
        {
            var live = new Dictionary<long, int> { { 123, 1071101 } };
            var snapshot = new[] { 1071101 };
            Assert.Single(Players.PreferLiveBuffs(live.Values, () => snapshot));

            // 游戏删除当前容器里的护盾，Hero.Buffs 快照仍保留旧条目。
            live.Remove(123);
            bool snapshotRead = false;
            var result = Players.PreferLiveBuffs(live.Values, () =>
            {
                snapshotRead = true;
                return snapshot;
            });
            Assert.Empty(result);
            Assert.False(snapshotRead);
        }

        [Fact]
        public void LiveContainer_UsesUpdatedBuffsInsteadOfOldSnapshot()
        {
            var live = new Dictionary<long, int> { { 123, 10006 } };
            var result = Players.PreferLiveBuffs(live.Values, () => new[] { 1071101 });
            Assert.Equal(new[] { 10006 }, result.ToArray());
        }

        [Fact]
        public void MissingLiveContainer_FallsBackToSnapshot()
        {
            var result = Players.PreferLiveBuffs<int>(null, () => new[] { 1071101 });
            Assert.Equal(new[] { 1071101 }, result.ToArray());
        }
    }
}
