// 复制自 mods/PveWaveForecastMod/WaveSchedule.cs（作者 CesiumLoader，同一仓库、同一许可证），只改了命名空间。
// 合并面板替代「PVE 波次预告」后，波次表由这里提供；改动时两边一起看。
using System;
using System.Collections.Generic;
using Core.Unit;
using GameLogic;

namespace PvePanelMod.Waves
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
