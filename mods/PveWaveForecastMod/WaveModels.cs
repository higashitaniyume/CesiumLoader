using System.Collections.Generic;

namespace PveWaveForecastMod
{
    internal enum WaveCategory
    {
        Unknown = 0,
        Normal = 1,
        Elite = 2,
        Boss = 3,
        Mixed = 4
    }

    internal sealed class MonsterTag
    {
        public int Id;
        public string Name = "";
        public WaveCategory Category = WaveCategory.Unknown;
        public int Count = 1;
    }

    internal sealed class WaveInfo
    {
        public int EventId;
        public int CardId;
        public int Progress;
        public string Title = "";
        public string Description = "";
        public readonly List<MonsterTag> Monsters = new List<MonsterTag>();
        public readonly List<int> LandIds = new List<int>();
        public readonly List<int> OrderedMonsterIds = new List<int>();
        public readonly Dictionary<int, List<int>> MonstersByLand = new Dictionary<int, List<int>>();
        public bool ExactLands;
        public bool RandomLands;
        public bool SpawnsMonsters = true;

        public WaveCategory Category
        {
            get
            {
                WaveCategory highest = WaveCategory.Unknown;
                for (int i = 0; i < Monsters.Count; i++)
                {
                    var category = Monsters[i].Category;
                    if (category > highest) highest = category;
                }
                return highest;
            }
        }

        public string ShortLine
        {
            get
            {
                string title = string.IsNullOrEmpty(Title) ? "波次 " + EventId : Title;
                return "R" + Progress + " " + title;
            }
        }

        public int TotalMonsterCount
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Monsters.Count; i++) total += Monsters[i].Count > 0 ? Monsters[i].Count : 1;
                return total;
            }
        }

        public string MonsterLine
        {
            get
            {
                if (Monsters.Count == 0) return "";
                var sorted = new List<MonsterTag>(Monsters);
                for (int i = 0; i < sorted.Count; i++)
                {
                    for (int j = i + 1; j < sorted.Count; j++)
                    {
                        if (sorted[j].Category > sorted[i].Category)
                        {
                            var tmp = sorted[i];
                            sorted[i] = sorted[j];
                            sorted[j] = tmp;
                        }
                    }
                }

                var parts = new List<string>();
                for (int i = 0; i < sorted.Count && i < 4; i++)
                {
                    var monster = sorted[i];
                    string count = monster.Count > 1 ? "×" + monster.Count : "";
                    parts.Add(CategoryPrefix(monster.Category) + monster.Name + count);
                }
                if (sorted.Count > 4) parts.Add("…");
                return string.Join(" / ", parts.ToArray());
            }
        }

        /// <summary>
        /// Per-land label. Only exact waves keep a one-monster-per-land assignment;
        /// random fallbacks deliberately return an empty string so the map does not
        /// imply that a single land spawns the whole wave.
        /// </summary>
        public string MonsterLineForLand(int landId)
        {
            List<int> ids;
            if (!MonstersByLand.TryGetValue(landId, out ids) || ids.Count == 0) return "";

            var unique = new List<int>();
            var counts = new List<int>();
            for (int i = 0; i < ids.Count; i++)
            {
                int index = unique.IndexOf(ids[i]);
                if (index < 0) { unique.Add(ids[i]); counts.Add(1); }
                else counts[index]++;
            }

            var parts = new List<string>();
            for (int i = 0; i < unique.Count; i++)
            {
                var tag = TagOf(unique[i]);
                string name = tag != null ? tag.Name : "#" + unique[i];
                var category = tag != null ? tag.Category : WaveCategory.Unknown;
                string count = counts[i] > 1 ? "×" + counts[i] : "";
                parts.Add(CategoryPrefix(category) + name + count);
            }
            return string.Join(" / ", parts.ToArray());
        }

        public MonsterTag TagOf(int id)
        {
            for (int i = 0; i < Monsters.Count; i++)
                if (Monsters[i].Id == id) return Monsters[i];
            return null;
        }

        private static string CategoryPrefix(WaveCategory category)
        {
            if (category == WaveCategory.Boss) return "首领·";
            if (category == WaveCategory.Elite) return "精英·";
            if (category == WaveCategory.Normal) return "普通·";
            return "";
        }
    }
}
