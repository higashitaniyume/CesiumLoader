using System;
using System.Collections.Generic;
using System.Text;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Runtime;
using CesiumLoader.SDK.UserInterface;
using GameLogic;
using Tools;
using party.model;

namespace CombatOddsMod
{
    public static partial class ModEntry
    {
        /// <summary>
        /// 读目标身上的实时 buff, 换算成普通投牌战斗的受伤修正(易伤/减伤/免疫)。
        /// <paramref name="sourceHeroId"/> 是这一击的施加者角色 id(战斗里的攻方 HeroId),
        /// 供【渊蚀印记】这类只对特定来源生效的 buff 判断是否计入; 未知传 0。
        /// </summary>
        private static CombatMath.BuffAdjustment BuffAdjOf(long targetId, CombatMath.DamageContext context = CombatMath.DamageContext.NormalBattle, long causeId = 0, bool targetIsMonster = true, int sourceHeroId = 0)
        {
            // MapEvent damage (notably Garden Courtyard's Phoenix skill) is already
            // server-resolved and must not be treated as ordinary received damage.
            if (context == CombatMath.DamageContext.MapEvent)
            {
                return new CombatMath.BuffAdjustment
                {
                    Delta = 0,
                    Immune = false,
                    Applied = new List<string>(),
                    ContextOnly = new List<string>(),
                };
            }

            try
            {
                var tb = TargetBuffsOf(targetId);
                if (tb.Count > 0)
                    return CombatMath.EvaluateTargetBuffs(tb, _buffTable, context, causeId, targetIsMonster, sourceHeroId);
            }
            catch { }
            return new CombatMath.BuffAdjustment
            {
                Delta = 0,
                Immune = false,
                Applied = new List<string>(),
                ContextOnly = new List<string>(),
            };
        }

        /// <summary>
        /// 读取目标身上的 buff 与<b>原始进度</b>。SDK 的 <see cref="Players.BuffsOf"/> 会把进度 0
        /// 兜底成 1 层(方便展示), 但游戏在层数归零后仍可能保留该条目: 幽魂暗巷的科学怪人初始
        /// 3 层【深度改造】, 三个人工生命体被击倒各移除 1 层后只剩 0 层图标, 而 BOSS 已经可以
        /// 被攻击。照兜底层数算会一直显示"目标免疫这一击", 所以这里直接读 <c>Buff.Progress</c>。
        /// 数据源顺序与 SDK 一致: 实时容器 → Hero 快照 → SDK 列表(最后兜底)。
        /// </summary>
        private static List<CombatMath.TargetBuff> TargetBuffsOf(long targetId)
        {
            var list = new List<CombatMath.TargetBuff>();
            bool found = false;
            try
            {
                // 与游戏 buff 界面同源的实时容器优先; 容器存在但为空 = 真的没有 buff(不能回退旧快照)。
                var live = Players.Get(targetId)?.buffContainer?._buffDict;
                if (live != null)
                {
                    foreach (var pair in live)
                    {
                        var b = pair.Value;
                        if (b == null) continue;
                        list.Add(new CombatMath.TargetBuff(b.BuffId, b.Progress));
                    }
                    found = true;
                }
            }
            catch { }

            if (!found)
            {
                try
                {
                    var hero = Players.Get(targetId)?.player?.Hero ?? RoomHeroOf(targetId);
                    if (hero?.Buffs != null)
                    {
                        foreach (var pair in hero.Buffs)
                        {
                            var b = pair.Value;
                            if (b == null) continue;
                            list.Add(new CombatMath.TargetBuff(b.BuffId, b.Progress));
                        }
                        found = true;
                    }
                }
                catch { }
            }

            if (!found)
            {
                // 两条路都取不到时退回 SDK 列表(层数已兜底 ≥1)。
                try
                {
                    foreach (var b in Players.BuffsOf(targetId))
                        list.Add(new CombatMath.TargetBuff(b.BuffId, b.Layers));
                }
                catch { }
            }

            // 标记(10006)不在 buff 容器里, 单独从 MarkNum/MarkCount 补一条。
            return CombatMath.WithMarkLayers(list, MarkLayersOf(targetId));
        }

        /// <summary>
        /// 标记层数。游戏把标记存在 <c>Hero.MarkNum</c>, 战斗内实时值为
        /// <c>BattleProperty.MarkCount</c>(buff 10006 的进度), 不在 buffContainer / Hero.Buffs 里。
        /// 读不到时返回 0, 不影响其他 buff。
        /// </summary>
        private static int MarkLayersOf(long unitId)
        {
            try
            {
                var pd = Players.Get(unitId);
                if (pd?.Property?.MarkCount != null)
                {
                    // 实时属性是权威值：标记被消耗后这里是 0，不能再回退到 Hero.MarkNum 的旧快照。
                    return Math.Max(0, pd.Property.MarkCount.property.Value);
                }
            }
            catch { }
            try
            {
                var hero = Players.Get(unitId)?.player?.Hero ?? RoomHeroOf(unitId);
                if (hero != null) return Math.Max(0, hero.MarkNum);
            }
            catch { }
            return 0;
        }

        /// <summary>不在战斗玩家表里的单位(怪物/观战对象)从房间花名册取 Hero 快照。</summary>
        private static Hero RoomHeroOf(long unitId)
        {
            try
            {
                var room = SimpleSingletonProvider<GameLogicManager>.inst?.room?.curRoomInfo;
                if (room == null) return null;
                if (room.Players != null)
                    foreach (var p in room.Players)
                        if (p != null && p.Id == unitId && p.Hero != null) return p.Hero;
                if (room.Monsters != null)
                    foreach (var m in room.Monsters)
                        if (m != null && m.Id == unitId && m.Hero != null) return m.Hero;
            }
            catch { }
            return null;
        }


        private static int HpOf(long playerId)
        {
            try
            {
                var pd = Players.Get(playerId);
                if (pd != null && pd.Property != null && pd.Property.HP != null)
                    return pd.Property.HP.Value;
            }
            catch { }
            // 回退: 观战他人战斗时, 对手/怪物可能不在 battle.PlayerDatas 里,
            // 从房间花名册(含 Players + Monsters)按 id 找当前血量。
            try
            {
                foreach (var s in Players.Roster())
                    if (s.Id == playerId) return s.Hp;
            }
            catch { }
            return 0;
        }

        private static string SafeName(BattleRole role)
        {
            try { return Names.BattleRole(role); }
            catch { return role != null ? ("P" + role.PlayerId) : "?"; }
        }

        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
    }
}
