#nullable disable
using System;
using System.Collections.Generic;

namespace CharacterSkinMod
{
    internal sealed class LayoutAliasCaches<T> where T : class
    {
        private sealed class Entry { public IDictionary<string, T> Cache; public string Key; public T Value; public SkinOffsetConfigure Expected; }
        private readonly List<IDictionary<string, T>> _seen = new List<IDictionary<string, T>>();
        private readonly List<Entry> _entries = new List<Entry>();
        public void Sync(IDictionary<string, T> cache, IEnumerable<KeyValuePair<string, SkinLayoutAliases.Alias>> aliases,
            Func<SkinOffsetConfigure, T> create, Func<T, SkinOffsetConfigure, bool> matches)
        {
            if (cache == null) return;
            foreach (var seen in _seen) if (ReferenceEquals(seen, cache)) return;
            foreach (var pair in aliases)
            {
                if (cache.TryGetValue(pair.Key, out var value))
                {
                    if (!matches(value, pair.Value.Value)) throw new InvalidOperationException("私有图片布局缓存地址已被占用: " + pair.Key);
                }
                else { value = create(pair.Value.Value); cache.Add(pair.Key, value); }
                _entries.Add(new Entry { Cache = cache, Key = pair.Key, Value = value, Expected = pair.Value.Value.Clone() });
            }
            _seen.Add(cache);
        }
        public void Restore(Func<T, SkinOffsetConfigure, bool> matches)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.Cache.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry.Value) && matches(current, entry.Expected)) entry.Cache.Remove(entry.Key);
            }
            _entries.Clear(); _seen.Clear();
        }
    }
    // Private image URLs must retain the original image-keyed UI layout, not only its pixels.
    internal sealed class SkinLayoutAliases
    {
        internal sealed class Alias
        {
            public string Source;
            public SkinOffsetConfigure Value;
        }
        private sealed class Addition
        {
            public SkinConfigure Config;
            public SkinOffsetConfigure Value;
            public SkinOffsetConfigure Snapshot;
        }
        private readonly Dictionary<string, Alias> _aliases = new Dictionary<string, Alias>(StringComparer.Ordinal);
        private readonly List<Addition> _additions = new List<Addition>();
        public IEnumerable<KeyValuePair<string, Alias>> Entries => _aliases;
        public int Count => _aliases.Count;

        internal static SkinStandingPaintingConfigureItem PackageSource(List<SkinStandingPaintingConfigureItem> originals, int skinItemId)
        {
            // Toys CharacterImageSize uses the first skin when a package targets all skins.
            return skinItemId == 0 ? (originals.Count == 0 ? null : originals[0]) : originals.Find(skin => skin.ItemID == skinItemId);
        }
        internal static string ImageKey(SkinStandingPaintingConfigureItem skin, string slot)
        {
            if (skin == null) return "";
            if (slot == "InGameCharacter") return string.IsNullOrEmpty(skin.InGameCharacter) ? skin.Character : skin.InGameCharacter;
            if (slot == "InGameCharacterSFW") return string.IsNullOrEmpty(skin.InGameCharacterSFW) ? skin.SfwCharacter : skin.InGameCharacterSFW;
            if (slot.StartsWith("Bust", StringComparison.Ordinal) || slot.StartsWith("SfwBust", StringComparison.Ordinal))
            {
                var values = slot.StartsWith("Sfw", StringComparison.Ordinal) ? skin.SfwBust : skin.Bust;
                int index = slot.EndsWith("1", StringComparison.Ordinal) ? 1 : 0;
                return index < values.Count ? values[index] : "";
            }
            var property = typeof(SkinStandingPaintingConfigureItem).GetProperty(slot);
            return property == null ? "" : property.GetValue(skin, null) as string ?? "";
        }
        // Package pixels were exported/cropped against its own SkinItemId. If that layout
        // is unavailable, use the explicit official base, then the target's original layout.
        public void Register(string alias, string slot, SkinConfigure config,
            SkinStandingPaintingConfigureItem packageSource, SkinStandingPaintingConfigureItem officialSource,
            SkinStandingPaintingConfigureItem original)
        {
            foreach (var source in new[] { packageSource, officialSource, original })
            {
                string key = ImageKey(source, slot);
                if (key.Length == 0) continue;
                foreach (var layout in config.Offsets)
                {
                    if (layout.ImageName != key) continue;
                    var value = layout.Clone();
                    typeof(SkinOffsetConfigure).GetProperty("ImageName").SetValue(value, alias, null);
                    if (_aliases.TryGetValue(alias, out var existing))
                    {
                        if (!existing.Value.Equals(value)) throw new InvalidOperationException("自定义图片布局冲突: " + alias);
                        return;
                    }
                    _aliases.Add(alias, new Alias { Source = key, Value = value });
                    return;
                }
            }
        }
        public void Apply(SkinConfigure config)
        {
            foreach (var pair in _aliases)
            {
                foreach (var value in config.Offsets)
                    if (value.ImageName == pair.Key) throw new InvalidOperationException("私有图片布局地址已存在: " + pair.Key);
                var addition = new Addition { Config = config, Value = pair.Value.Value, Snapshot = pair.Value.Value.Clone() };
                _additions.Add(addition);
                config.Offsets.Add(addition.Value);
            }
            // OffsetDict is keyed by numeric configuration ID. Game consumers build their
            // image-name lookup from Offsets, so never insert invented IDs into OffsetDict.
        }
        public void Restore()
        {
            for (int i = _additions.Count - 1; i >= 0; i--)
            {
                var addition = _additions[i];
                if (!addition.Value.Equals(addition.Snapshot)) continue;
                for (int j = addition.Config.Offsets.Count - 1; j >= 0; j--)
                    if (ReferenceEquals(addition.Config.Offsets[j], addition.Value)) { addition.Config.Offsets.RemoveAt(j); break; }
            }
            _additions.Clear();
        }
    }
}
