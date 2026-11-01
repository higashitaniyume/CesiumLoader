using System;
using System.Collections.Generic;

namespace PveWaveForecastMod
{
    /// <summary>
    /// Pure config-side wave schedule. No Unity types, so it can be tested off-line
    /// and reused by the runtime adapter in <see cref="WaveSchedule"/>.
    /// </summary>
    internal static class WaveScheduleCore
    {
        // Exact land sets observed in replays; used only when the config has no
        // one-to-one Params2 land list. Key = (mapId << 32) ^ eventId.
        private static readonly Dictionary<long, int[]> KnownLands = new Dictionary<long, int[]>
        {
            { Key(82017, 32131), new[] { 35, 11, 21, 25 } },
            { Key(82017, 32132), new[] { 4, 38, 52 } },
            { Key(82017, 32133), new[] { 31, 15, 23 } },
            { Key(82017, 32134), new[] { 31 } },
            { Key(82013, 32050), new[] { 41, 33, 22, 52, 36, 19 } }
        };

        private static readonly Dictionary<long, Dictionary<int, int>> KnownAssignments = new Dictionary<long, Dictionary<int, int>>
        {
            { Key(82017, 32131), Assign(35, 1074, 11, 1074, 21, 1074, 25, 1072) },
            { Key(82017, 32132), Assign(4, 1071, 38, 1073, 52, 1074) },
            { Key(82017, 32133), Assign(31, 1073, 15, 1073, 23, 1072) },
            { Key(82013, 32050), Assign(41, 1028, 33, 1028, 22, 1028, 52, 1028, 36, 1032, 19, 1031) }
        };

        private static Dictionary<int, int> Assign(params int[] pairs)
        {
            var map = new Dictionary<int, int>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) map[pairs[i]] = pairs[i + 1];
            return map;
        }

        internal static List<WaveInfo> Build(int mapId, int mapDifficultyId, int difficulty, int maxWaves,
            Func<int, bool> landExists, IList<int> monsterLands)
        {
            var result = new List<WaveInfo>();
            if (StaticConfigure.Map == null || StaticConfigure.Map.GameDifficultyDict == null) return result;
            if (StaticConfigure.MapEvent == null || StaticConfigure.MapEvent.InfoDict == null) return result;

            MapGameDifficultyConfigure row;
            if (!StaticConfigure.Map.GameDifficultyDict.TryGetValue(mapDifficultyId, out row) || row == null)
                return result;

            var items = row.MapGameDifficultyConfigureItems;
            if (items == null || difficulty < 0 || difficulty >= items.Count) return result;
            var item = items[difficulty];
            if (item == null) return result;

            var events = item.ProgressEvents;
            if (events == null) return result;

            var seen = new List<int>();
            for (int i = 0; i < events.Count; i++)
            {
                int eventId = events[i];
                if (seen.Contains(eventId)) continue;
                seen.Add(eventId);

                MapEventInfoConfigure ev;
                if (!StaticConfigure.MapEvent.InfoDict.TryGetValue(eventId, out ev) || ev == null) continue;

                int progress = ev.Triggerparams != null && ev.Triggerparams.Count > 0 ? ev.Triggerparams[0] : 0;
                if (progress < 1 || progress > item.ProgressLimit) continue;

                var wave = new WaveInfo
                {
                    EventId = eventId,
                    CardId = ev.CardID,
                    Progress = progress,
                    Title = LocalMapEvent(ev.CardID),
                    Description = LocalMapEvent(ev.CardID * 10 + 1)
                };

                CollectMonsters(ev, item, wave);
                ResolveLands(mapId, eventId, ev, wave, landExists, monsterLands);
                AssignLands(mapId, eventId, wave);

                result.Add(wave);
            }

            for (int i = 0; i < result.Count; i++)
            {
                for (int j = i + 1; j < result.Count; j++)
                {
                    if (result[j].Progress < result[i].Progress ||
                        (result[j].Progress == result[i].Progress && result[j].EventId < result[i].EventId))
                    {
                        var tmp = result[i];
                        result[i] = result[j];
                        result[j] = tmp;
                    }
                }
            }

            if (result.Count > maxWaves) result.RemoveRange(maxWaves, result.Count - maxWaves);
            return result;
        }

        private sealed class SpawnCount
        {
            public int Id;
            public int Count;
        }

        private sealed class DescriptionMatch
        {
            public int Index;
            public int Id;
            public int Count;
        }

        private static void CollectMonsters(MapEventInfoConfigure ev, MapGameDifficultyConfigureItem item, WaveInfo wave)
        {
            bool hasMonsterParam = false;
            var fromParams = new List<SpawnCount>();
            if (ev.Params1 != null)
            {
                for (int i = 0; i < ev.Params1.Count; i++)
                {
                    int id = ev.Params1[i];
                    if (!IsMonster(id)) continue;
                    hasMonsterParam = true;
                    int count = 1;
                    if (i + 1 < ev.Params1.Count)
                    {
                        int next = ev.Params1[i + 1];
                        if (!IsMonster(next) && next > 0 && next <= 20)
                        {
                            count = next;
                            i++;
                        }
                    }
                    AddSpawn(fromParams, id, count);
                }
            }

            var fromDescription = ParseDescriptionSpawns(wave.Description);
            var chosen = fromDescription.Count > 0 ? fromDescription : fromParams;

            // The description sometimes names only the headliner ("刷新4个蟹布罗")
            // while Params1 still carries the accompanying elites. Append those.
            if (fromDescription.Count > 0)
            {
                for (int i = 0; i < fromParams.Count; i++)
                {
                    if (!HasSpawn(chosen, fromParams[i].Id))
                        AddSpawn(chosen, fromParams[i].Id, fromParams[i].Count);
                }
            }

            if (ev.MapEventEffectConfigs != null)
            {
                foreach (var pair in ev.MapEventEffectConfigs)
                {
                    if (pair.Key != 6) continue; // ActiveBoss
                    var values = pair.Value != null ? pair.Value.Values : null;
                    if (values == null) continue;
                    for (int i = 0; i < values.Count; i++)
                    {
                        int id = values[i];
                        if (IsMonster(id) && !HasSpawn(chosen, id)) AddSpawn(chosen, id, 1);
                    }
                }
            }

            if (chosen.Count == 0 && item.MonsterIds != null)
            {
                foreach (var pair in item.MonsterIds)
                {
                    if (chosen.Count >= 4) break;
                    if (IsMonster(pair.Key) && !HasSpawn(chosen, pair.Key)) AddSpawn(chosen, pair.Key, 1);
                }
            }

            for (int i = 0; i < chosen.Count; i++)
            {
                var spawn = chosen[i];
                MergeMonster(wave, spawn.Id, spawn.Count);
                for (int n = 0; n < spawn.Count; n++) wave.OrderedMonsterIds.Add(spawn.Id);
            }

            wave.SpawnsMonsters = hasMonsterParam || ContainsAny(wave.Description, "刷新", "登场", "出场", "召唤", "出现");
        }

        private static void AddSpawn(List<SpawnCount> list, int id, int count)
        {
            if (count < 1) count = 1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Id != id) continue;
                list[i].Count += count;
                return;
            }
            list.Add(new SpawnCount { Id = id, Count = count });
        }

        private static bool HasSpawn(List<SpawnCount> list, int id)
        {
            string name = MonsterName(id);
            for (int i = 0; i < list.Count; i++)
                if (list[i].Id == id || MonsterName(list[i].Id) == name) return true;
            return false;
        }

        private static void MergeMonster(WaveInfo wave, int id, int count)
        {
            if (count < 1) count = 1;
            string name = MonsterName(id);
            for (int i = 0; i < wave.Monsters.Count; i++)
            {
                if (wave.Monsters[i].Id != id && wave.Monsters[i].Name != name) continue;
                if (count > wave.Monsters[i].Count) wave.Monsters[i].Count = count;
                return;
            }
            wave.Monsters.Add(new MonsterTag
            {
                Id = id,
                Name = name,
                Category = CategoryOf(id),
                Count = count
            });
        }

        /// <summary>
        /// Match monster names in the wave description, longest name first, and blank
        /// out matched text so "孩子王嘎呜" does not also match the shorter "嘎呜".
        /// Matches are returned in their original text order.
        /// </summary>
        private static List<SpawnCount> ParseDescriptionSpawns(string description)
        {
            var result = new List<SpawnCount>();
            if (string.IsNullOrEmpty(description) || StaticConfigure.Monster == null || StaticConfigure.Monster.InfoDict == null)
                return result;

            var candidates = new List<MonsterTag>();
            foreach (var pair in StaticConfigure.Monster.InfoDict)
            {
                string name = MonsterName(pair.Key);
                if (string.IsNullOrEmpty(name) || name.Length < 2 || name[0] == '#') continue;
                candidates.Add(new MonsterTag { Id = pair.Key, Name = name });
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    if (candidates[j].Name.Length > candidates[i].Name.Length)
                    {
                        var tmp = candidates[i];
                        candidates[i] = candidates[j];
                        candidates[j] = tmp;
                    }
                }
            }

            var text = description;
            var matches = new List<DescriptionMatch>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                int index = text.IndexOf(candidate.Name, StringComparison.Ordinal);
                if (index < 0) continue;

                matches.Add(new DescriptionMatch
                {
                    Index = index,
                    Id = candidate.Id,
                    Count = CountBefore(text, index)
                });

                var chars = text.ToCharArray();
                for (int k = index; k < index + candidate.Name.Length && k < chars.Length; k++) chars[k] = ' ';
                text = new string(chars);
            }

            for (int i = 0; i < matches.Count; i++)
            {
                for (int j = i + 1; j < matches.Count; j++)
                {
                    if (matches[j].Index < matches[i].Index)
                    {
                        var tmp = matches[i];
                        matches[i] = matches[j];
                        matches[j] = tmp;
                    }
                }
            }

            for (int i = 0; i < matches.Count; i++)
                AddSpawn(result, matches[i].Id, matches[i].Count);
            return result;
        }

        private static void AssignLands(int mapId, int eventId, WaveInfo wave)
        {
            wave.MonstersByLand.Clear();
            if (!wave.ExactLands || wave.LandIds.Count == 0 || wave.OrderedMonsterIds.Count == 0) return;

            Dictionary<int, int> exact;
            if (KnownAssignments.TryGetValue(Key(mapId, eventId), out exact))
            {
                for (int i = 0; i < wave.LandIds.Count; i++)
                {
                    int id;
                    if (!exact.TryGetValue(wave.LandIds[i], out id)) continue;
                    List<int> knownIds;
                    if (!wave.MonstersByLand.TryGetValue(wave.LandIds[i], out knownIds))
                    {
                        knownIds = new List<int>();
                        wave.MonstersByLand[wave.LandIds[i]] = knownIds;
                    }
                    knownIds.Add(id);
                }
                return;
            }

            int landCount = wave.LandIds.Count;
            for (int i = 0; i < wave.OrderedMonsterIds.Count; i++)
            {
                int landId = wave.LandIds[i % landCount];
                List<int> ids;
                if (!wave.MonstersByLand.TryGetValue(landId, out ids))
                {
                    ids = new List<int>();
                    wave.MonstersByLand[landId] = ids;
                }
                ids.Add(wave.OrderedMonsterIds[i]);
            }
        }

        private static int CountBefore(string text, int nameIndex)
        {
            int p = nameIndex - 1;
            if (p < 0 || (text[p] != '个' && text[p] != '只')) return 1;
            int end = p;
            int start = end;
            while (start > 0 && char.IsDigit(text[start - 1])) start--;
            if (start >= end) return 1;
            int count;
            if (int.TryParse(text.Substring(start, end - start), out count) && count > 0 && count <= 99) return count;
            return 1;
        }

        private static bool ContainsAny(string text, params string[] words)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < words.Length; i++)
                if (text.IndexOf(words[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static void ResolveLands(int mapId, int eventId, MapEventInfoConfigure ev, WaveInfo wave,
            Func<int, bool> landExists, IList<int> monsterLands)
        {
            if (!wave.SpawnsMonsters) return;
            int[] known;
            if (KnownLands.TryGetValue(Key(mapId, eventId), out known))
            {
                AddValidLands(wave.LandIds, known, landExists);
                int need = wave.TotalMonsterCount > 0 ? wave.TotalMonsterCount : 1;
                if (wave.LandIds.Count >= need)
                {
                    wave.ExactLands = true;
                    return;
                }
                wave.LandIds.Clear();
            }

            if (TryParams2(ev.Params2, wave.TotalMonsterCount, landExists, wave.LandIds))
            {
                wave.ExactLands = true;
                return;
            }

            wave.RandomLands = true;
            if (monsterLands != null)
            {
                for (int i = 0; i < monsterLands.Count; i++)
                {
                    int id = monsterLands[i];
                    if (landExists == null || landExists(id)) wave.LandIds.Add(id);
                }
            }
        }

        private static bool TryParams2(IList<int> params2, int monsterCount, Func<int, bool> landExists, List<int> output)
        {
            if (params2 == null || monsterCount <= 0 || params2.Count < monsterCount) return false;
            for (int i = 0; i < monsterCount; i++)
            {
                int id = params2[i];
                if (landExists != null && !landExists(id)) return false;
                if (output.Contains(id)) return false;
                output.Add(id);
            }
            return output.Count == monsterCount;
        }

        private static void AddValidLands(List<int> output, int[] ids, Func<int, bool> landExists)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];
                if (landExists != null && !landExists(id)) continue;
                if (!output.Contains(id)) output.Add(id);
            }
        }

        private static bool IsMonster(int id)
        {
            return StaticConfigure.Monster != null
                && StaticConfigure.Monster.InfoDict != null
                && StaticConfigure.Monster.InfoDict.ContainsKey(id);
        }

        private static string MonsterName(int id)
        {
            MonsterInfoConfigure info;
            if (!StaticConfigure.Monster.InfoDict.TryGetValue(id, out info) || info == null) return "#" + id;
            STRMonsterLocalConfigure local;
            if (StaticConfigure.STRMonster != null
                && StaticConfigure.STRMonster.LocalDict != null
                && StaticConfigure.STRMonster.LocalDict.TryGetValue(info.NameID, out local)
                && local != null
                && !string.IsNullOrEmpty(local.Simplified))
            {
                return local.Simplified;
            }
            return "#" + id;
        }

        private static WaveCategory CategoryOf(int id)
        {
            MonsterInfoConfigure info;
            if (!StaticConfigure.Monster.InfoDict.TryGetValue(id, out info) || info == null) return WaveCategory.Unknown;
            if (info.MonsterType == MonsterType.Boss) return WaveCategory.Boss;
            if (info.MonsterType == MonsterType.Elite) return WaveCategory.Elite;
            if (info.MonsterType == MonsterType.Normal) return WaveCategory.Normal;
            return WaveCategory.Unknown;
        }

        private static string LocalMapEvent(int id)
        {
            STRMapEventLocalConfigure local;
            if (StaticConfigure.STRMapEvent != null
                && StaticConfigure.STRMapEvent.LocalDict != null
                && StaticConfigure.STRMapEvent.LocalDict.TryGetValue(id, out local)
                && local != null
                && !string.IsNullOrEmpty(local.Simplified))
            {
                return local.Simplified;
            }
            return "";
        }

        private static long Key(int mapId, int eventId)
        {
            return ((long)mapId << 32) ^ (uint)eventId;
        }
    }
}
