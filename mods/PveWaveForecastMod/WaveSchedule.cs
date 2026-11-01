using System;
using System.Collections.Generic;
using Core.Unit;
using GameLogic;

namespace PveWaveForecastMod
{
    /// <summary>Runtime adapter: reads the current room and land manager, then delegates to the pure core.</summary>
    internal static class WaveSchedule
    {
        internal static List<WaveInfo> Build(RoomInfo room, LandManager lands, int maxWaves)
        {
            if (room == null || lands == null || lands.NodeDict == null) return new List<WaveInfo>();

            var monsterLands = new List<int>();
            foreach (var pair in lands.NodeDict)
            {
                var land = pair.Value;
                if (land == null) continue;
                if (land.LandType == LandType.Monster) monsterLands.Add(pair.Key);
            }

            return WaveScheduleCore.Build(
                room.MapId,
                room.MapDifficultyId,
                room.Difficulty,
                maxWaves,
                landId => lands.NodeDict.ContainsKey(landId),
                monsterLands);
        }
    }
}
