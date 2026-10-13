using System;
using System.Collections.Generic;
using System.IO;
using FairyGUI;

namespace CardSkinMod
{
    /// <summary>Shared clock; current and previous decoded frames shared by cards with the same resource key.</summary>
    public sealed class CardAnimationPlayer : IDisposable
    {
        private sealed class Sequence
        {
            public CardTextureLoader Loader = new CardTextureLoader();
            public string Previous;
            public long LastUsed;
        }
        private readonly Dictionary<string, Sequence> _cache = new Dictionary<string, Sequence>(StringComparer.Ordinal);
        private readonly HashSet<string> _failed = new HashSet<string>(StringComparer.Ordinal);
        private readonly long _epoch = DateTime.UtcNow.Ticks / 10000;
        public NTexture GetFrame(CardAnimation animation, string assetKey = null)
        {
            string key = animation.ManifestPath + "\n" + (assetKey ?? string.Empty);
            if (_failed.Contains(animation.ManifestPath)) return null;
            long now = DateTime.UtcNow.Ticks / 10000;
            if (!_cache.TryGetValue(key, out var sequence)) _cache[key] = sequence = new Sequence();
            string file = animation.FramePath(animation.FrameAt(now - _epoch));
            NTexture texture = null;
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0
                    && (File.GetAttributes(Path.GetDirectoryName(file)) & FileAttributes.ReparsePoint) == 0)
                    texture = sequence.Loader.GetOrCreateTexture(file, assetKey, animation.Width, animation.Height);
            }
            catch { }
            if (texture == null || texture.width != animation.Width || texture.height != animation.Height)
            {
                _failed.Add(animation.ManifestPath);
                return null;
            }
            sequence.Loader.RetainFiles(file, sequence.Previous);
            sequence.Previous = file;
            sequence.LastUsed = now;
            return texture;
        }
        public bool OwnsTexture(NTexture texture)
        {
            if (texture == null || texture.disposed) return false;
            foreach (var sequence in _cache.Values) if (sequence.Loader.OwnsTexture(texture)) return true;
            return false;
        }
        public void Sweep()
        {
            long now = DateTime.UtcNow.Ticks / 10000;
            var expired = new List<string>();
            foreach (var pair in _cache) if (now - pair.Value.LastUsed > 2000) expired.Add(pair.Key);
            foreach (string key in expired) { _cache[key].Loader.Dispose(); _cache.Remove(key); }
        }
        public void Dispose()
        {
            foreach (var sequence in _cache.Values) sequence.Loader.Dispose();
            _cache.Clear();
            _failed.Clear();
        }
    }
}
