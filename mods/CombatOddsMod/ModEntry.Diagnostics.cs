using System;
using System.Text;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using GameLogic;
using Tools;

namespace CombatOddsMod
{
    public static partial class ModEntry
    {
#if COMBATODDS_DEBUG_PROBES
        private static void LogPhoenixProbe(party.protocol.UpdateHeroAttrS2C update)
        {
            try
            {
                bool phoenixCause = update.Cause.S == party.protocol.CauseOrigin.Types.source.Skill && update.Cause.Id == 105311;
                bool hasAsh = false;
                foreach (var effect in update.EffectDatas)
                {
                    if (effect?.PlayerId > 0 && HasAsh(effect.PlayerId)) hasAsh = true;
                    if (effect?.Hp != null && HasAsh(effect.Hp.PlayerId)) hasAsh = true;
                }
                if (!phoenixCause && !hasAsh) return;

                var room = SimpleSingletonProvider<GameLogicManager>.inst?.room?.curRoomInfo;
                var sb = new StringBuilder("[凤凰探针] cause=" + update.Cause.S + "#" + update.Cause.Id +
                    "|playerId=" + update.PlayerId +
                    "|mapType=" + (room != null ? room.MapType : 0) +
                    "|mapId=" + (room != null ? room.MapId : 0) +
                    "|round=" + (room != null ? room.Round : 0));
                foreach (var effect in update.EffectDatas)
                {
                    if (effect == null) continue;
                    if (effect.Hp != null)
                    {
                        var hp = effect.Hp;
                        sb.Append("|HP[").Append(hp.PlayerId)
                            .Append(" ").Append(hp.OriHp).Append("->").Append(hp.CurrHp)
                            .Append(" real=").Append(hp.RealChangeHp)
                            .Append(" damageType=").Append(hp.DamageType)
                            .Append(" killer=").Append(hp.Killer).Append(']');
                    }
                    if (effect.Buff != null)
                    {
                        var change = effect.Buff;
                        sb.Append("|BUFF[").Append(change.PlayerId)
                            .Append(" op=").Append(change.Op)
                            .Append(" one=").Append(FormatBuff(change.Buff))
                            .Append(" map=").Append(FormatBuffMap(change.Buffs)).Append(']');
                    }
                }
                SdkLog.Info("CombatOdds", sb.ToString());
            }
            catch (Exception e)
            {
                SdkLog.Warn("CombatOdds", "[凤凰探针] 读取失败: " + e.Message);
            }
        }

        private static bool HasAsh(long playerId)
        {
            try
            {
                var pd = Players.Get(playerId);
                var dict = pd?.buffContainer?._buffDict;
                if (dict == null) return false;
                foreach (var pair in dict)
                    if (pair.Value != null && pair.Value.BuffId == 10531201) return true;
            }
            catch { }
            return false;
        }

        private static string FormatBuff(party.model.Buff buff)
        {
            if (buff == null) return "null";
            return "uid=" + buff.UniqueId + ",id=" + buff.BuffId + ",progress=" + buff.Progress +
                ",useTime=" + buff.UseTime + ",index=" + buff.BuffIndex +
                ",keep=" + buff.KeepRound + ",params=" + FormatLongMap(buff.Params) +
                ",restore=" + FormatLongMap(buff.RestoreParams);
        }

        private static string FormatBuffMap(Google.Protobuf.Collections.MapField<long, party.model.Buff> buffs)
        {
            if (buffs == null || buffs.Count == 0) return "{}";
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var pair in buffs)
            {
                if (!first) sb.Append(';');
                first = false;
                sb.Append(pair.Key).Append(':').Append(FormatBuff(pair.Value));
            }
            return sb.Append('}').ToString();
        }

        private static void LogAshDiagnostic(long targetId, CombatMath.BuffAdjustment adjustment)
        {
            try
            {
                const int ashId = 10531201;
                var pd = Players.Get(targetId);
                var dict = pd?.buffContainer?._buffDict;
                if (dict == null) return;

                party.model.Buff ash = null;
                foreach (var pair in dict)
                {
                    if (pair.Value != null && pair.Value.BuffId == ashId)
                    {
                        ash = pair.Value;
                        break;
                    }
                }

                var room = SimpleSingletonProvider<GameLogicManager>.inst?.room?.curRoomInfo;
                int mapType = room != null ? room.MapType : 0;
                int mapId = room != null ? room.MapId : 0;
                int round = room != null ? room.Round : 0;
                string state;
                if (ash == null)
                {
                    state = "absent|mapType=" + mapType + "|mapId=" + mapId + "|round=" + round;
                }
                else
                {
                    state = "uid=" + ash.UniqueId +
                        "|progress=" + ash.Progress +
                        "|buffIndex=" + ash.BuffIndex +
                        "|keepRound=" + ash.KeepRound +
                        "|useTime=" + ash.UseTime +
                        "|params=" + FormatLongMap(ash.Params) +
                        "|restoreParams=" + FormatLongMap(ash.RestoreParams) +
                        "|mapType=" + mapType +
                        "|mapId=" + mapId +
                        "|round=" + round +
                        "|modLayers=" + FindAshLayers(adjustment) +
                        "|modDelta=" + adjustment.Delta;
                }

                string previous;
                if (_lastAshDiagnostic.TryGetValue(targetId, out previous) && previous == state) return;
                _lastAshDiagnostic[targetId] = state;
                SdkLog.Info("CombatOdds", "[灰烬诊断] target=" + targetId + " " + state);
            }
            catch (Exception e)
            {
                SdkLog.Warn("CombatOdds", "[灰烬诊断] 读取失败: " + e.Message);
            }
        }

        private static string FindAshLayers(CombatMath.BuffAdjustment adjustment)
        {
            if (adjustment.Applied == null) return "?";
            foreach (var item in adjustment.Applied)
            {
                if (item != null && item.StartsWith("灰烬")) return item;
            }
            return "none";
        }

        private static string FormatLongMap(Google.Protobuf.Collections.MapField<int, long> values)
        {
            if (values == null || values.Count == 0) return "{}";
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var pair in values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(pair.Key).Append(':').Append(pair.Value);
            }
            return sb.Append('}').ToString();
        }
#endif
    }
}
