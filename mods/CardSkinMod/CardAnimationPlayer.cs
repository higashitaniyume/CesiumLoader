using System;
using System.Collections.Generic;
using System.IO;
using FairyGUI;
using UnityEngine;

namespace CardSkinMod
{
    /// <summary>Whole-sequence admission, incremental decode and a shared animation clock.</summary>
    public sealed class CardAnimationPlayer : IDisposable
    {
        private sealed class Sequence
        {
            public readonly CardTextureLoader Loader = new CardTextureLoader();
            public CardAnimation Animation;
            public string AssetKey;
            public NTexture[] Frames;
            public string[] Files;
            public int Loaded;
            public int Desired;
            public int Current = -1;
            public int Previous = -1;
            public int Prepared = -1;
            public int Ready = -1;
            public int Prefetch;
            public long FrameBytes;
            public long LastUsed;
            public bool KeepWhole;
            public bool SizeKnown;
        }
        private readonly Dictionary<string, Sequence> _cache = new Dictionary<string, Sequence>(StringComparer.Ordinal);
        private readonly List<Sequence> _sequences = new List<Sequence>();
        private readonly List<Sequence> _admissionOrder = new List<Sequence>();
        private readonly HashSet<string> _failed = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<NTexture> _owned = new HashSet<NTexture>();
        private readonly List<string> _expired = new List<string>();
        private readonly long _epoch = DateTime.UtcNow.Ticks / 10000;
        private readonly long _budget;
        private bool _admissionDirty;
        private long _nextSweep;
        private long _updateTime;
        private int _pumpCursor;

        public CardAnimationPlayer(int cacheBudgetMiB = 512)
        {
            if (cacheBudgetMiB < 64) cacheBudgetMiB = 64;
            if (cacheBudgetMiB > 1024) cacheBudgetMiB = 1024;
            _budget = cacheBudgetMiB * 1024L * 1024;
        }
        public void BeginUpdate(long now) { _updateTime = now; }
        private long Now => _updateTime != 0 ? _updateTime : DateTime.UtcNow.Ticks / 10000;
        public int FrameIndex(CardAnimation animation) => animation.FrameAt(Now - _epoch);
        public bool IsFailed(CardAnimation animation) => _failed.Contains(animation.ManifestPath);

        public NTexture GetFrame(CardAnimation animation, string assetKey = null, string sequenceKey = null)
        {
            if (_failed.Contains(animation.ManifestPath)) return null;
            string key = sequenceKey ?? (animation.ManifestPath + "\n" + (assetKey ?? string.Empty));
            if (!_cache.TryGetValue(key, out var sequence))
            {
                CardTextureLoader.EnsureMethodsResolved();
                sequence = new Sequence {
                    Animation = animation, AssetKey = assetKey,
                    Frames = new NTexture[animation.FrameCount], Files = new string[animation.FrameCount],
                    FrameBytes = (long)animation.Width * animation.Height * (CardTextureLoader.LoadsNonReadable ? 4 : 8)
                };
                _cache[key] = sequence;
                _sequences.Add(sequence);
                _admissionDirty = true;
            }
            sequence.LastUsed = Now;
            sequence.Desired = FrameIndex(animation);
            if (Valid(sequence, sequence.Desired))
            {
                if (sequence.Current != sequence.Desired)
                {
                    sequence.Previous = sequence.Current;
                    sequence.Current = sequence.Desired;
                }
                sequence.Ready = -1;
                if (!sequence.KeepWhole) TrimStreaming(sequence);
                return sequence.Frames[sequence.Current];
            }
            // At very low game FPS the clock may advance before a prepared demand frame is shown.
            if (Valid(sequence, sequence.Ready))
            {
                sequence.Previous = sequence.Current;
                sequence.Current = sequence.Ready;
                sequence.Ready = -1;
                if (!sequence.KeepWhole) TrimStreaming(sequence);
                return sequence.Frames[sequence.Current];
            }
            // Keep displaying a valid frame while the decoder prepares the desired frame.
            if (Valid(sequence, sequence.Current)) return sequence.Frames[sequence.Current];
            if (Valid(sequence, sequence.Prepared))
            {
                sequence.Previous = sequence.Current;
                sequence.Current = sequence.Prepared;
                return sequence.Frames[sequence.Current];
            }
            return null;
        }

        private bool Valid(Sequence sequence, int index)
        {
            if (index < 0) return false;
            var texture = sequence.Frames[index];
            if (texture == null) return false;
            if (!texture.disposed && texture.nativeTexture != null) return true;
            Release(sequence, index);
            return false;
        }

        /// <summary>Call once per game frame, after all visible cards have registered their demand.</summary>
        public void Pump()
        {
            if (_admissionDirty) Rebalance();
            int count = _sequences.Count;
            for (int visited = 0; visited < count; visited++)
            {
                if (_pumpCursor >= count) _pumpCursor = 0;
                var sequence = _sequences[_pumpCursor++];
                if (Now - sequence.LastUsed > 500 || _failed.Contains(sequence.Animation.ManifestPath)) continue;
                int index = sequence.Desired;
                if (Valid(sequence, index))
                {
                    if (!sequence.KeepWhole || sequence.Loaded == sequence.Frames.Length) continue;
                    index = -1;
                    for (int scanned = 0; scanned < sequence.Frames.Length; scanned++)
                    {
                        int candidate = sequence.Prefetch++;
                        if (sequence.Prefetch >= sequence.Frames.Length) sequence.Prefetch = 0;
                        if (!Valid(sequence, candidate)) { index = candidate; break; }
                    }
                    if (index < 0) continue;
                }
                Load(sequence, index);
                // PNG decoding and Unity allocation stay on the engine thread, at most one per tick.
                break;
            }
        }

        private void Load(Sequence sequence, int index)
        {
            var animation = sequence.Animation;
            string file = sequence.Files[index] ?? (sequence.Files[index] = animation.FramePath(index));
            NTexture texture = null;
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0
                    && (File.GetAttributes(Path.GetDirectoryName(file)) & FileAttributes.ReparsePoint) == 0)
                    texture = sequence.Loader.GetOrCreateTexture(file, sequence.AssetKey, animation.Width, animation.Height);
            }
            catch { }
            if (texture == null || texture.width != animation.Width || texture.height != animation.Height)
            {
                sequence.Loader.ReleaseFile(file, sequence.AssetKey);
                _failed.Add(animation.ManifestPath);
                return;
            }
            sequence.Frames[index] = texture;
            sequence.Loaded++;
            sequence.Prepared = index;
            if (index == sequence.Desired) sequence.Ready = index;
            _owned.Add(texture);
            {
                // Account for any later readable/mipmapped fallback too; never under-reserve it.
                long bytes = (long)animation.Width * animation.Height * 8;
                try
                {
                    if (texture.nativeTexture is Texture2D native)
                        bytes = (long)animation.Width * animation.Height * (native.isReadable ? 8 : 4)
                            * (native.mipmapCount > 1 ? 2 : 1);
                }
                catch { }
                if (!sequence.SizeKnown || bytes > sequence.FrameBytes)
                {
                    if (sequence.FrameBytes != bytes) _admissionDirty = true;
                    sequence.FrameBytes = bytes;
                    sequence.SizeKnown = true;
                }
            }
            if (!sequence.KeepWhole) TrimStreaming(sequence);
        }

        private void Rebalance()
        {
            _admissionDirty = false;
            _admissionOrder.Clear();
            _admissionOrder.AddRange(_sequences);
            long now = Now;
            _admissionOrder.Sort((left, right) => {
                bool leftActive = now - left.LastUsed <= 500, rightActive = now - right.LastUsed <= 500;
                if (leftActive != rightActive) return leftActive ? -1 : 1;
                long leftSize = left.FrameBytes * left.Frames.Length, rightSize = right.FrameBytes * right.Frames.Length;
                int size = leftSize.CompareTo(rightSize);
                return size != 0 ? size : right.LastUsed.CompareTo(left.LastUsed);
            });
            long reserved = 0;
            foreach (var sequence in _sequences)
                reserved += sequence.FrameBytes * Math.Min(3, sequence.Frames.Length);
            foreach (var sequence in _admissionOrder)
            {
                long extra = sequence.FrameBytes * (sequence.Frames.Length - Math.Min(3, sequence.Frames.Length));
                bool whole = extra <= _budget - reserved;
                if (whole) reserved += extra;
                sequence.KeepWhole = whole;
                if (!whole) TrimStreaming(sequence);
            }
        }

        private void TrimStreaming(Sequence sequence)
        {
            for (int i = 0; i < sequence.Frames.Length; i++)
                if (i != sequence.Current && i != sequence.Previous && i != sequence.Prepared && sequence.Frames[i] != null)
                    Release(sequence, i);
        }
        private void Release(Sequence sequence, int index)
        {
            var texture = sequence.Frames[index];
            if (texture == null) return;
            _owned.Remove(texture);
            sequence.Loader.ReleaseFile(sequence.Files[index], sequence.AssetKey);
            sequence.Frames[index] = null;
            sequence.Loaded--;
        }
        public bool OwnsTexture(NTexture texture) => texture != null && !texture.disposed && _owned.Contains(texture);

        public void Sweep()
        {
            long now = Now;
            if (now < _nextSweep) return;
            _nextSweep = now + 1000;
            _expired.Clear();
            foreach (var pair in _cache) if (now - pair.Value.LastUsed > 2000) _expired.Add(pair.Key);
            foreach (string key in _expired)
            {
                var sequence = _cache[key];
                foreach (var frame in sequence.Frames) if (frame != null) _owned.Remove(frame);
                sequence.Loader.Dispose();
                _sequences.Remove(sequence);
                _cache.Remove(key);
            }
            // Reclaim inactive reservations before admitting new visible sequences.
            if (_expired.Count > 0) _admissionDirty = true;
        }
        public void Dispose()
        {
            foreach (var sequence in _sequences) sequence.Loader.Dispose();
            _cache.Clear();
            _sequences.Clear();
            _admissionOrder.Clear();
            _failed.Clear();
            _owned.Clear();
            _expired.Clear();
        }
    }
}
