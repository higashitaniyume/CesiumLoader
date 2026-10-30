using ChatDurationMod;
using Xunit;

namespace ChatDurationMod.Tests
{
    public class MessageStayTrackerTests
    {
        [Fact]
        public void Observe_StartsClockAtFirstScan()
        {
            var tracker = new MessageStayTracker();

            MessageStayTracker.Entry entry = tracker.Observe("a", 100.0);

            Assert.Equal(100.0, entry.FirstSeen);
            Assert.False(entry.Expired);
            Assert.False(tracker.IsDue("a", 114.9, 15));
            Assert.True(tracker.IsDue("a", 115.0, 15));
        }

        [Fact]
        public void Observe_ReturnsExistingClock()
        {
            var tracker = new MessageStayTracker();

            tracker.Observe("a", 10.0);
            MessageStayTracker.Entry entry = tracker.Observe("a", 20.0);

            Assert.Equal(10.0, entry.FirstSeen);
        }

        [Fact]
        public void MarkShown_ResetsClockForPooledReuse()
        {
            var tracker = new MessageStayTracker();
            tracker.Observe("a", 0.0);

            Assert.True(tracker.IsDue("a", 15.0, 15));

            tracker.MarkShown("a", 15.0);

            Assert.False(tracker.IsDue("a", 15.0, 15));
            Assert.False(tracker.IsDue("a", 29.9, 15));
            Assert.True(tracker.IsDue("a", 30.0, 15));
        }

        [Fact]
        public void MarkExpired_StopsDueUntilShownAgain()
        {
            var tracker = new MessageStayTracker();
            tracker.Observe("a", 0.0);
            tracker.MarkExpired("a");

            Assert.False(tracker.IsDue("a", 100.0, 15));

            tracker.MarkShown("a", 100.0);

            Assert.False(tracker.IsDue("a", 100.0, 15));
            Assert.True(tracker.IsDue("a", 115.0, 15));
        }

        [Fact]
        public void Prune_DropsObjectsNoLongerInList()
        {
            var tracker = new MessageStayTracker();
            tracker.Observe("a", 0.0);
            tracker.Observe("b", 0.0);
            tracker.Observe("c", 0.0);

            tracker.Prune(new object[] { "a", "c" });

            Assert.Equal(2, tracker.Count);
            Assert.True(tracker.TryGet("a", out _));
            Assert.False(tracker.TryGet("b", out _));
            Assert.True(tracker.TryGet("c", out _));
        }

        [Fact]
        public void Prune_EmptyListClearsEverything()
        {
            var tracker = new MessageStayTracker();
            tracker.Observe("a", 0.0);

            tracker.Prune(new object[0]);

            Assert.Equal(0, tracker.Count);
        }

        [Fact]
        public void SnapshotKeys_ReturnsEveryTrackedObject()
        {
            var tracker = new MessageStayTracker();
            tracker.Observe("a", 0.0);
            tracker.Observe("b", 0.0);

            object[] keys = tracker.SnapshotKeys();

            Assert.Equal(2, keys.Length);
            Assert.Contains("a", keys);
            Assert.Contains("b", keys);
        }
    }
}
