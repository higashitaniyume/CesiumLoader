using System;
using System.Collections.Generic;

namespace CombatOddsMod
{
    public static partial class CombatMath
    {
        // —— 实时可读, 数据来源 Buff.bin/STRBuff 反编译
        // =====================================================================
        //
        // 针对【目标身上实时可读的 buff】: 服务器把每个单位的激活 buff 放在
        //   Hero.Buffs(MapField<long,Buff>), 每条含 BuffId + Progress(层数)。mod 通过
        //   Players.BuffsOf(unitId) 读到后, 用下表把"受到伤害±N"类效果换算成对该单位这一击的伤害修正。
        //   标记(10006)例外: 它不在 buff 列表里, 而是 Hero.MarkNum / BattleProperty.MarkCount,
        //   由 WithMarkLayers 补成合成条目后再走同一张表。
        //
        // 关键结论(反编译 465 条 Buff 说明, buffcat 工具逐条提取):
        //   · "受到伤害/受到的伤害 ±N" 只有下表这些 buff 携带; 攻击力加成(如狂暴+3攻、逆鳞+2攻)
        //     一般已并入服务器下发的 BattleRole.Atk, 故本表【只取受伤侧的 ±N】, 不重复加攻。
        //   · 标记(10006)= 受到伤害+1（固定，不按层数叠加）；层数是剩余持续/消耗次数。
        //   · 卡牌【狂暴】(卡 20008 → buff 2000801)= 每层攻+3、受伤+1, 可叠多层。
        //   · 免疫类(护盾 1071101 下次-99、深度改造 10331101 受伤降为0)按"这一击伤害归零"处理。
        //   · 语境限定类只对特定来源的伤害生效: 精准打击只吃【轨道轰炸】，渊蚀印记只吃
        //     【魔渊触须/赛克斯】。它们的 GeneralCombat=false，命中来源时按层数计入，
        //     未命中时只作展示提示、不改数值；两种结果都可据日志核对。

        public enum DamageContext
        {
            NormalBattle,
            Skill,
            Card,
            Destiny,
            Divination,
            Event,
            Land,
            Buff,
            Summon,
            Relic,
            MapEvent,
            Unknown
        }

        /// <summary>把服务端 DamageType 转为求值语境。</summary>
        public static DamageContext ContextFromDamageType(int damageType)
        {
            switch (damageType)
            {
                case 1: return DamageContext.Skill;
                case 2: return DamageContext.Card;
                case 3: return DamageContext.Destiny;
                case 4: return DamageContext.Divination;
                case 5: return DamageContext.Event;
                case 6: return DamageContext.Land;
                case 7: return DamageContext.NormalBattle;
                case 8: return DamageContext.Buff;
                case 9: return DamageContext.Summon;
                case 10: return DamageContext.Relic;
                case 11: return DamageContext.MapEvent;
                default: return DamageContext.Unknown;
            }
        }

        /// <summary>语境限定 Buff 是否适用于当前伤害来源。</summary>
        /// <param name="context">服务端 DamageType 对应的伤害语境。</param>
        /// <param name="causeId">伤害来源 id(技能/召唤物/事件等，取决于服务端 Cause.S)。</param>
        /// <param name="sourceHeroId">施加这一击的单位角色 id(普通投牌战斗里的攻方 HeroId)；未知传 0。</param>
        public static bool AppliesInContext(BuffDamageEffect effect, DamageContext context, long causeId = 0, int sourceHeroId = 0)
        {
            if (effect == null || effect.GeneralCombat) return true;
            if (effect.BuffId == 1221202) return context == DamageContext.Skill && causeId == 12203;
            if (effect.BuffId == AbyssMarkBuffId)
                return IsSeaxDamageCause(sourceHeroId) || IsSeaxDamageCause(causeId);
            return false;
        }

        /// <summary>
        /// 该 id 是否属于赛克斯的一套(赛克斯本人或她召唤的魔渊触须)。【渊蚀印记】只对这一套生效：
        ///   · 129 —— 赛克斯本人(角色 id)；普通投牌战斗里攻方 <c>BattleRole.HeroId</c> 就是它；
        ///   · 12901/12902 —— 赛克斯的主动技能(PVP/PVE，召唤魔渊触须并转化魔渊区域)；
        ///   · 12903/12904 —— 魔渊触须激活时执行的 perform(Summon 12911/12912 的 CommonPerforms)；
        ///   · 12911/12912 —— 魔渊触须召唤物本身(Summon.bin，PrefabName=Summon_129_Tentacle)。
        /// 服务端把哪个 id 写进 <c>Cause.Id</c> 取决于这一击由技能还是召唤物触发，故整组都认。
        /// </summary>
        public static bool IsSeaxDamageCause(long id)
        {
            switch (id)
            {
                case SeaxHeroId:
                case 12901:
                case 12902:
                case 12903:
                case 12904:
                case 12911:
                case 12912:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>梅加斯轨道轰炸：每丢弃两张牌触发一次。</summary>
        public static int MegasOrbitalBombardmentCount(int discardedCards)
        {
            return discardedCards > 0 ? discardedCards / 2 : 0;
        }

        /// <summary>
        /// 单次轨道轰炸伤害 = 3 + 总费用 ÷ 3，向上取整（例：总费用 14 → 3 + ⌈4.67⌉ = 8）。
        /// 没有触发次数时不产生伤害。
        /// </summary>
        public static int MegasOrbitalBombardmentDamage(int discardedCards, int discardedCost)
        {
            if (MegasOrbitalBombardmentCount(discardedCards) == 0) return 0;
            if (discardedCost < 0) discardedCost = 0;
            return 3 + (discardedCost + 2) / 3;
        }

        /// <summary>梅加斯轨道轰炸理论总伤害 = 开炮次数 × 单炮伤害。</summary>
        public static int MegasOrbitalBombardmentTotalDamage(int discardedCards, int discardedCost)
        {
            return MegasOrbitalBombardmentCount(discardedCards) * MegasOrbitalBombardmentDamage(discardedCards, discardedCost);
        }

        /// <summary>邦妮攻击带标记(10006)怪物时的额外攻击力。</summary>
        public static int BonnieMarkedMonsterAttackBonus(int heroId, bool targetIsMonster, bool targetMarked)
        {
            return heroId == 127 && targetIsMonster && targetMarked ? 3 : 0;
        }

        /// <summary>日志与协议已确认的角色技能标签；仅用于来源说明，不代表未知公式已被猜测。</summary>
        public static string KnownSkillLabel(long causeId)
        {
            switch (causeId)
            {
                case 12203: return "梅加斯·轨道轰炸";
                case 12902: return "赛克斯·魔域转化";
                case 11403: return "蓝海晴·虚弱印记";
                case 12702: return "邦妮·隐匿行动";
                default: return null;
            }
        }

        /// <summary>一条 buff 的伤害修正规则(针对"该 buff 拥有者被击中时"的受伤)。</summary>
        public sealed class BuffDamageEffect
        {
            public int BuffId;
            public string Name;
            /// <summary>受伤侧每单位的伤害增量(正=易伤, 负=减伤)。Immunity=true 时忽略。</summary>
            public int PerUnit;
            /// <summary>true=按层数(Progress)累加; false=一次性(与层数无关)。</summary>
            public bool PerLayer;
            /// <summary>
            /// true=作用于任意来源的伤害; false=语境限定(只对特定来源的伤害生效)。语境限定的
            /// buff 命中来源时照常计入(可按 <see cref="PerLayer"/> 叠层)，未命中时只进
            /// <c>ContextOnly</c> 作展示提示，不改数值。
            /// </summary>
            public bool GeneralCombat;
            /// <summary>true=把这一击伤害直接归零(护盾/免疫)。</summary>
            public bool Immunity;
            /// <summary>
            /// true=该效果靠层数维持: 层数归零后即使图标还在(见 Buff.bin 的
            /// IsShowIconProgressZero)也不再生效。一次性 buff(护盾等)用 false —— 它们
            /// 的进度不是生效条件, 不能被"进度 0"误判成已结束。
            /// </summary>
            public bool LayerScoped;
            /// <summary>单次受伤上限(0 = 无上限), 例如宝藏桶"每次最多受到1点伤害"。</summary>
            public int DamageCap;
            public string Note;
            public BuffDamageEffect(int id, string name, int perUnit, bool perLayer, bool general, bool immunity, string note, bool layerScoped = false, int damageCap = 0)
            { BuffId = id; Name = name; PerUnit = perUnit; PerLayer = perLayer; GeneralCombat = general; Immunity = immunity; Note = note; LayerScoped = layerScoped; DamageCap = damageCap; }
        }

        /// <summary>
        /// buffId → 受伤修正规则。来自 Buff.bin + STRBuff 反编译(buffcat 工具), 每条都能对到一句
        /// 明确的"受到伤害±N"游戏说明, 故均为确定数值, 命中即精确计入。
        /// </summary>
        public static Dictionary<int, BuffDamageEffect> DefaultBuffTable()
        {
            var t = new Dictionary<int, BuffDamageEffect>();
            void Add(BuffDamageEffect e) { t[e.BuffId] = e; }

            // —— 易伤(受到伤害+) ——
            Add(new BuffDamageEffect(10006,   "标记",     +1, false, true,  false, "被标记目标受到伤害+1; 层数只影响持续时间", layerScoped: true));
            Add(new BuffDamageEffect(2000801, "狂暴",     +1, true,  true,  false, "卡牌狂暴: 每层攻击+3、受到伤害+1"));
            Add(new BuffDamageEffect(1140101, "宿命回响", +1, false, true,  false, "蓝海晴PVP: 受到伤害+1, 2回合"));
            Add(new BuffDamageEffect(1140102, "虚弱印记", +1, false, true,  false, "蓝海晴PVE: 怪物受到伤害+1, 2回合"));
            Add(new BuffDamageEffect(4000401, "霉运",     +2, false, true,  false, "下次受到伤害+2"));
            Add(new BuffDamageEffect(3200501, "易伤",     +2, false, true,  false, "下次受到伤害+2"));
            Add(new BuffDamageEffect(10671302,"真凶",     +2, false, true,  false, "攻+2/移+2/出牌+1, 受到伤害+2"));
            Add(new BuffDamageEffect(10141101,"脆弱中枢", +1, false, true,  false, "每次受伤本轮受到伤害+1(条件性, 近似取+1)"));

            // —— 减伤(受到伤害-) ——
            Add(new BuffDamageEffect(4000301, "好运",     -2, false, true,  false, "下次受到伤害-2"));
            Add(new BuffDamageEffect(1261101, "湖沼之王", -1, true,  true,  false, "每层攻+1、受到伤害-1"));
            Add(new BuffDamageEffect(5006001, "逆鳞",     -2, true,  true,  false, "每层攻+2、受到伤害-2"));
            Add(new BuffDamageEffect(10261101,"逆鳞",     -2, true,  true,  false, "每层攻+2、受到伤害-2"));
            Add(new BuffDamageEffect(5007002, "大铜锣",   -1, false, true,  false, "攻+1、受到伤害-1"));
            Add(new BuffDamageEffect(10531201,"灰烬",     -1, true,  true,  false, "每层受到伤害-1"));
            Add(new BuffDamageEffect(10551101,"稳定中枢", -1, true,  true,  false, "每层受到伤害-1"));

            // —— 免疫/护盾(这一击归零) ——
            Add(new BuffDamageEffect(1071101, "护盾",     0, false, true,  true,  "下次受到伤害-99, 视为挡下这一击"));
            Add(new BuffDamageEffect(1071102, "护盾",    -3, false, true,  false, "下次受到伤害-3"));
            // 幽魂暗巷科学怪人: 初始 3 层, 每击倒一个人工生命体移除 1 层。层数归零后图标
            // 仍显示(IsShowIconProgressZero), 但 BOSS 已经可以被攻击, 免疫必须随层数一起结束。
            Add(new BuffDamageEffect(10331101,"深度改造", 0, false, true,  true,  "受到伤害降为0(层数归零后失效)", layerScoped: true));

            // —— 单次受伤上限 ——
            Add(new BuffDamageEffect(10591101,"铁箍宝藏桶", 0, false, true, false, "每次最多受到1点伤害", damageCap: 1));

            // —— 语境限定(只对特定来源生效, 命中时按层数计入) ——
            Add(new BuffDamageEffect(1221202, "精准打击", +1, false, false, false, "只对【轨道轰炸】伤害+1"));
            // 赛克斯 PVE 被动(技能 12912)：自身造成伤害时给目标叠 1 层, 每层使目标受到
            // 来自魔渊触须(召唤物 12911/12912)与赛克斯(角色 129)的伤害 +1, 持续 1 轮。
            Add(new BuffDamageEffect(AbyssMarkBuffId, "渊蚀印记", +1, true, false, false, "魔渊触须/赛克斯的伤害"));

            return t;
        }

        /// <summary>目标身上一条 buff 的实例(id + 层数)。用于喂给 <see cref="EvaluateTargetBuffs"/>。</summary>
        public struct TargetBuff
        {
            public int BuffId;
            /// <summary>buff 的原始进度(游戏里图标上的层数)。0 = 已归零: 效果结束, 图标可能仍在。</summary>
            public int Layers;
            public TargetBuff(int id, int layers) { BuffId = id; Layers = layers; }
        }

        /// <summary>
        /// 标记(10006)在游戏里不是普通 Buff: 它存放在 <c>Hero.MarkNum</c> /
        /// <c>BattleProperty.MarkCount</c>, <c>buffContainer</c> 与 <c>Hero.Buffs</c> 里都没有
        /// 10006 条目。把层数补成一条合成条目, 供受伤修正与"带标记"判定使用; 已有 10006 时不重复。
        /// </summary>
        public static List<TargetBuff> WithMarkLayers(IEnumerable<TargetBuff> buffs, int markLayers)
        {
            var list = buffs != null ? new List<TargetBuff>(buffs) : new List<TargetBuff>();
            if (markLayers <= 0) return list;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].BuffId != 10006) continue;
                if (list[i].Layers < markLayers)
                    list[i] = new TargetBuff(10006, markLayers);
                return list;
            }
            list.Add(new TargetBuff(10006, markLayers));
            return list;
        }

        /// <summary>
        /// 摩西 PVE 被动【精密技巧】的当前层数: 只认 heroId=125 且 buff 1251201,
        /// 层数限制在 0..3。每层使闪避最小骰点 +1, 所以闪避骰下限 = 层数 + 1。
        /// </summary>
        public static int MosesDodgeStacks(int heroId, IEnumerable<TargetBuff> buffs)
        {
            if (heroId != MosesHeroId || buffs == null) return 0;
            int layers = 0;
            foreach (var b in buffs)
                if (b.BuffId == PrecisionBuffId && b.Layers > layers) layers = b.Layers;
            return layers > MaxPrecisionStacks ? MaxPrecisionStacks : layers;
        }

        /// <summary>对目标 buff 求值后的伤害修正。</summary>
        public struct BuffAdjustment
        {
            /// <summary>普通投牌战斗下, 该目标这一击的净受伤增量(正=易伤, 负=减伤)。</summary>
            public int Delta;
            /// <summary>是否免疫这一击(伤害归零)。</summary>
            public bool Immune;
            /// <summary>单次受伤上限(0 = 无上限)。</summary>
            public int MaxDamagePerHit;
            /// <summary>已计入的效果标签(如 "标记x2 +2")。</summary>
            public List<string> Applied;
            /// <summary>识别到但不计入普通投牌结算的语境限定效果(如 "精准打击")。</summary>
            public List<string> ContextOnly;
        }

        /// <summary>
        /// 用 buff 表对目标身上的 buff 求值, 得到普通投牌战斗下的受伤修正。
        /// 未在表中的 buff 一律忽略(绝大多数 buff 不影响伤害)。
        /// </summary>
        public static BuffAdjustment EvaluateTargetBuffs(IEnumerable<TargetBuff> targetBuffs, Dictionary<int, BuffDamageEffect> table)
        {
            return EvaluateTargetBuffs(targetBuffs, table, DamageContext.NormalBattle);
        }

        public static BuffAdjustment EvaluateTargetBuffs(IEnumerable<TargetBuff> targetBuffs, Dictionary<int, BuffDamageEffect> table, DamageContext context, long causeId = 0)
        {
            return EvaluateTargetBuffs(targetBuffs, table, context, causeId, true);
        }

        /// <param name="sourceHeroId">
        /// 造成这一击的单位角色 id(普通投牌战斗里的攻方 HeroId)，未知传 0。
        /// 语境限定 buff(如渊蚀印记只吃赛克斯的伤害)靠它判断来源。
        /// </param>
        public static BuffAdjustment EvaluateTargetBuffs(IEnumerable<TargetBuff> targetBuffs, Dictionary<int, BuffDamageEffect> table, DamageContext context, long causeId, bool targetIsMonster, int sourceHeroId = 0)
        {
            var adj = new BuffAdjustment
            {
                Delta = 0,
                Immune = false,
                MaxDamagePerHit = 0,
                Applied = new List<string>(),
                ContextOnly = new List<string>(),
            };
            if (targetBuffs == null || table == null) return adj;

            foreach (var tb in targetBuffs)
            {
                if (!table.TryGetValue(tb.BuffId, out var eff) || eff == null) continue;
                int layers = Math.Max(0, tb.Layers);   // 原始进度, 0 = 已归零

                // 层数归零 = 效果结束。游戏会为部分 buff 保留 0 层图标(IsShowIconProgressZero),
                // 例如幽魂暗巷科学怪人的【深度改造】被三个人工生命体各削掉 1 层后仍是 0 层条目;
                // 若继续按"至少 1 层"算, 会一直把一只已可攻击的 BOSS 判成免疫。
                // 一次性 buff(护盾等)的进度不是生效条件, 不在此列。
                if (layers == 0 && (eff.PerLayer || eff.LayerScoped)) continue;

                if (eff.BuffId == 1140102 && !targetIsMonster)
                {
                    adj.ContextOnly.Add(eff.Name + "（仅怪物）");
                    continue;
                }
                if (!eff.GeneralCombat && !AppliesInContext(eff, context, causeId, sourceHeroId))
                {
                    adj.ContextOnly.Add(eff.Name + "（仅" + eff.Note + "）");
                    continue;
                }
                if (eff.DamageCap > 0 && (adj.MaxDamagePerHit == 0 || eff.DamageCap < adj.MaxDamagePerHit))
                {
                    adj.MaxDamagePerHit = eff.DamageCap;
                    adj.Applied.Add(eff.Name + "(每击最多" + eff.DamageCap + ")");
                }
                if (eff.Immunity)
                {
                    adj.Immune = true;
                    adj.Applied.Add(eff.Name + "(免疫)");
                    continue;
                }

                int contrib = eff.PerLayer ? eff.PerUnit * layers : eff.PerUnit;
                adj.Delta += contrib;

                string tag = eff.Name;
                if (eff.PerLayer && layers > 1) tag += "x" + layers;
                tag += (contrib >= 0 ? " +" : " ") + contrib;
                adj.Applied.Add(tag);
            }

            return adj;
        }
    }
}
