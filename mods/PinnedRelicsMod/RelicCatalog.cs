using System;
using System.Collections.Generic;
using System.Text;

namespace PinnedRelicsMod
{
    internal sealed class RelicIcon
    {
        public readonly int Id;
        public readonly string Name;
        public RelicIcon(int id, string name)
        { Id = id; Name = name; }
    }

    internal static class RelicCatalog
    {
        internal const string DefaultNames = "肾上腺素-高效,磨刀石,友情徽章,安全气囊,紫色飞星,金色飞星,美工刀-初级,美工刀-锋利";
        private static readonly Dictionary<string, RelicIcon> ByName = Load();

        private static Dictionary<string, RelicIcon> Load()
        {
            var result = new Dictionary<string, RelicIcon>(StringComparer.Ordinal);
            // HybridCLR must not depend on Assembly.GetManifestResourceStream here.
            // Plain C# data also avoids StreamReader and numeric parsing during startup.
            foreach (var relic in RelicCatalogData.Create())
            {
                // Indexer, not Add: a duplicated name after a game hot update must not
                // break the static initializer (that would take the whole mod down).
                result[Normalize(relic.Name)] = relic;
            }
            // BWIKI spells this entry 微章; the game calls it 徽章.
            RelicIcon badge;
            if (result.TryGetValue(Normalize("友情徽章"), out badge))
                result[Normalize("友情微章")] = badge;
            return result;
        }

        internal static RelicIcon[] Select(string text, out string[] unknown)
        {
            var items = new List<RelicIcon>();
            var missing = new List<string>();
            var ids = new HashSet<int>();
            var seenMissing = new HashSet<string>(StringComparer.Ordinal);
            var parts = (text ?? "").Split(new[] { ',', '，', '、', ';', '；', '\r', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var key = Normalize(part);
                if (key.Length == 0) continue;
                RelicIcon relic;
                if (ByName.TryGetValue(key, out relic))
                {
                    if (ids.Add(relic.Id)) items.Add(relic);
                }
                else if (seenMissing.Add(key)) missing.Add(part.Trim());
            }
            unknown = missing.ToArray();
            return items.ToArray();
        }

        private static string Normalize(string value)
        {
            var result = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c)) continue;
                switch (c)
                {
                    case '－': case '—': case '–': case '‐': case '‑': result.Append('-'); break;
                    case '（': result.Append('('); break;
                    case '）': result.Append(')'); break;
                    default: result.Append(c); break;
                }
            }
            return result.ToString();
        }

        internal static int FillOwned(RelicIcon[] selection, IList<int> owned, bool[] visible)
        {
            int count = 0;
            for (int i = 0; i < selection.Length; i++)
            {
                bool found = false;
                if (owned != null)
                    for (int n = 0; n < owned.Count; n++)
                        if (owned[n] == selection[i].Id) { found = true; break; }
                visible[i] = found;
                if (found) count++;
            }
            return count;
        }

        internal static float FitScale(int count, float size, float gap, float available)
        {
            float width = count > 0 ? count * size + (count - 1) * gap : 0;
            return width > 0 ? Math.Min(1, Math.Max(0, available) / width) : 1;
        }
    }
}
