using System;
using System.Collections.Generic;

namespace ChatDurationMod
{
    /// <summary>
    /// 每条消息/表情条目的真实时间停留状态。
    ///
    /// 游戏用 <c>GAME_CHARACTER_EXPRESSION_DURATION</c>（毫秒）给自己计时，而该计时跟随
    /// 变速后的游戏时间；这里改用调用方传入的真实时间（<see cref="DateTime.UtcNow"/>），
    /// 并把时间戳按 UI 条目对象记录，因此每条消息都可以独立到点。
    ///
    /// 条目的对象可能被 FairyGUI 对象池复用：列表移除最旧条目后，同一帧里又用它显示新消息。
    /// 所以适配层会在收到新消息信号时调用 <see cref="MarkShown"/>，把复用对象重新计时。
    /// </summary>
    public sealed class MessageStayTracker
    {
        public struct Entry
        {
            /// <summary>首次显示（或最近一次重新显示）的真实时间，单位秒。</summary>
            public double FirstSeen;

            /// <summary>已经由本模组隐藏；对象被复用时由 MarkShown 清除。</summary>
            public bool Expired;
        }

        private readonly Dictionary<object, Entry> _entries = new Dictionary<object, Entry>();

        public int Count { get { return _entries.Count; } }

        /// <summary>取条目状态；不存在时以 now 为起点新建。</summary>
        public Entry Observe(object key, double now)
        {
            if (key == null) return new Entry();

            Entry entry;
            if (!_entries.TryGetValue(key, out entry))
            {
                entry = new Entry { FirstSeen = now, Expired = false };
                _entries[key] = entry;
            }
            return entry;
        }

        public bool TryGet(object key, out Entry entry)
        {
            if (key == null) { entry = new Entry(); return false; }
            return _entries.TryGetValue(key, out entry);
        }

        /// <summary>把条目视为“刚刚重新显示”，用于对象池复用或游戏重新显示。</summary>
        public void MarkShown(object key, double now)
        {
            if (key == null) return;
            _entries[key] = new Entry { FirstSeen = now, Expired = false };
        }

        /// <summary>标记本条已经隐藏；后续扫描保持隐藏，直到对象被复用或移除。</summary>
        public void MarkExpired(object key)
        {
            if (key == null) return;

            Entry entry;
            if (!_entries.TryGetValue(key, out entry)) return;
            entry.Expired = true;
            _entries[key] = entry;
        }

        /// <summary>是否已到停留时长；已隐藏或未跟踪的条目返回 false。</summary>
        public bool IsDue(object key, double now, double durationSeconds)
        {
            Entry entry;
            if (key == null || !_entries.TryGetValue(key, out entry) || entry.Expired) return false;
            return ChatDurationRules.IsDue(entry.FirstSeen, now, durationSeconds);
        }

        /// <summary>丢弃已经不在列表里的条目。</summary>
        public void Prune(ICollection<object> live)
        {
            if (_entries.Count == 0) return;
            if (live == null || live.Count == 0)
            {
                _entries.Clear();
                return;
            }

            var keep = new HashSet<object>(live);
            var stale = new List<object>();
            foreach (var pair in _entries)
            {
                if (!keep.Contains(pair.Key)) stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++) _entries.Remove(stale[i]);
        }

        /// <summary>当前跟踪对象的快照，供停用/卸载时逐个隐藏。</summary>
        public object[] SnapshotKeys()
        {
            var keys = new object[_entries.Count];
            _entries.Keys.CopyTo(keys, 0);
            return keys;
        }

        public void Clear()
        {
            _entries.Clear();
        }
    }
}
