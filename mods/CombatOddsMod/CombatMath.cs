using System;
using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>
    /// 战斗概率计算核心 —— <b>纯逻辑, 零游戏/SDK 依赖, 可完全离线单测</b>。
    ///
    /// 机制来源(反编译 party.model.BattleRole / UI.FightWindow / Global.bin 验证):
    ///  - 攻/防最终值 = Min..Max 范围内的随机整数 + 一颗判定骰；范围随机项暂按等概率建模。
    ///  - 防御方在攻方投骰后做"防御 / 闪避"二选一(FightLogic.ReadyFightChoice)。
    ///  - 防御: 受伤 = max(1, 最终攻击 − 防御总值); 防御总值 = 防御范围随机值 + 单颗判定骰。
    ///  - 闪避: 掷一颗判定骰(GAME_JUDGE_DICE_LIMIT=6, 即 d6), 点数 > 攻方骰点则闪避成功、不受伤;
    ///          攻方骰点 == 骰上限(6) 时需掷出 = 6 才成功(UI.FightWindow.RefreshDodgeInfo 的 ">"/"=" 语义)。
    ///          闪避失败则承受未防御的攻击。
    ///  - 击倒判定: 受伤 >= 当前血量 → 被击倒(血量归零)。
    ///
    /// 关于骰子分布(重要): 多颗骰子之和 <b>不是</b>均匀分布, 必须做卷积。
    /// 若只知道 Min/Max 而不知每颗骰子面数, 会退化成"均匀近似"(EstimateFromRange), 精度较低,
    /// 调用方应尽量提供每颗骰子的面数 (Dice[])。
    /// </summary>
    public static class CombatMath
    {
        /// <summary>游戏判定骰面数(Global.GAME_JUDGE_DICE_LIMIT), 闪避判定用。</summary>
        public const int DefaultJudgeDiceFaces = 6;

        /// <summary>摩西(枪匠)的角色 id。</summary>
        public const int MosesHeroId = 125;

        /// <summary>摩西 PVE 被动【精密技巧】的 buff id。</summary>
        public const int PrecisionBuffId = 1251201;

        /// <summary>【精密技巧】最高层数。</summary>
        public const int MaxPrecisionStacks = 3;

        // =====================================================================
        // 骰池分布
        // =====================================================================

        /// <summary>
        /// 计算骰池 (flat + 每颗骰子 1..faces) 的总和概率分布。
        /// 返回数组 dist, 其中 dist[i] 是总和为 (min + i) 的概率; min 通过 out 返回。
        /// dice 为空时返回确定值 flat。
        /// </summary>
        public static double[] Distribution(int flat, IReadOnlyList<int> diceFaces, out int min)
        {
            // 起点: 总和 = flat, 概率 1。
            var dist = new double[1] { 1.0 };
            int lo = flat;

            if (diceFaces != null)
            {
                foreach (int faces in diceFaces)
                {
                    if (faces <= 0) continue; // 忽略非法骰子(0 面/负面)
                    // 与 1..faces 的均匀分布卷积。
                    var next = new double[dist.Length + (faces - 1)];
                    double per = 1.0 / faces;
                    for (int i = 0; i < dist.Length; i++)
                    {
                        double p = dist[i];
                        if (p == 0.0) continue;
                        for (int f = 0; f < faces; f++)
                            next[i + f] += p * per;
                    }
                    dist = next;
                    lo += 1; // 每颗骰子最小加 1
                }
            }

            min = lo;
            return dist;
        }

        // =====================================================================
        // 防御
        // =====================================================================

        public struct DefendResult
        {
            /// <summary>期望受到的伤害。</summary>
            public double ExpectedDamage;
            /// <summary>被击倒(受伤 >= 血量)的概率, 0..1。</summary>
            public double KnockdownProb;
            /// <summary>最小可能受伤。</summary>
            public int MinDamage;
            /// <summary>最大可能受伤。</summary>
            public int MaxDamage;
            /// <summary>是否为区间均匀近似(缺少每颗骰子面数时为 true, 精度较低)。</summary>
            public bool Approx;
        }

        /// <summary>
        /// 防御结算。finalAttack = 攻方已锁定的最终攻击值(攻方先投骰, 此刻已知)。
        /// defenseFlat = 防御基础(InitDef + 已确定加成), defenseDice = 防御骰面数列表(每颗 1..faces)。
        /// defenderHp = 当前血量。
        /// minChip 为命中保底伤害，普通战斗默认 1；特殊效果在此后修正伤害。
        ///   默认基线 max(1,Atk-Def)；无特殊效果时，1 血目标选择防御必被击倒。
        /// maxDamage 为单次受伤上限(0 = 无上限)，例如宝藏桶"每次最多受到1点伤害"。
        /// </summary>
        public static DefendResult Defend(int finalAttack, int defenderHp, int defenseFlat, IReadOnlyList<int> defenseDice, int minChip = 1, int damageAdjust = 0, bool immune = false, int maxDamage = 0)
        {
            int min;
            var dist = Distribution(defenseFlat, defenseDice, out min);

            double expDmg = 0.0, knock = 0.0;
            int minDmg = int.MaxValue, maxDmg = int.MinValue;
            for (int i = 0; i < dist.Length; i++)
            {
                double p = dist[i];
                if (p == 0.0) continue;
                int def = min + i;
                int dmg = finalAttack - def;
                if (dmg < minChip) dmg = minChip; // 普通战斗命中保底
                if (dmg < 0) dmg = 0;
                dmg += damageAdjust;                 // buff 易伤/减伤修正
                if (dmg < 0) dmg = 0;
                if (maxDamage > 0 && dmg > maxDamage) dmg = maxDamage; // 单次受伤上限
                if (immune) dmg = 0;                 // 护盾/免疫: 这一击归零
                expDmg += p * dmg;
                if (dmg < minDmg) minDmg = dmg;
                if (dmg > maxDmg) maxDmg = dmg;
                if (defenderHp > 0 && dmg >= defenderHp) knock += p;
            }
            if (minDmg == int.MaxValue) { minDmg = 0; maxDmg = 0; }

            return new DefendResult
            {
                ExpectedDamage = expDmg,
                KnockdownProb = knock,
                MinDamage = minDmg,
                MaxDamage = maxDmg,
                Approx = false
            };
        }

        // =====================================================================
        // 闪避
        // =====================================================================

        public struct DodgeResult
        {
            /// <summary>闪避成功(不受伤)的概率, 0..1。</summary>
            public double SuccessProb;
            /// <summary>被击倒的概率, 0..1。</summary>
            public double KnockdownProb;
            /// <summary>期望受到的伤害。</summary>
            public double ExpectedDamage;
        }

        /// <summary>
        /// 闪避结算。attackerPoint = 攻方投出的骰点(闪避阈值)。judgeFaces = 判定骰面数(默认 6)。
        /// finalAttack = 攻方最终攻击; damageOnFail = 闪避失败时承受的伤害
        /// (通常为全额攻击，保底 1；由调用方按是否保留基础防御决定，特殊 buff 随后修正)。
        /// maxDamage 为单次受伤上限(0 = 无上限)。
        /// minPoint 为闪避骰的最小点数(默认 1)。摩西【精密技巧】把下限抬到 层数+1 时，
        /// 骰面变为 [minPoint, judgeFaces]；minPoint=1 与旧版完全一致。
        /// </summary>
        public static DodgeResult Dodge(int finalAttack, int defenderHp, int attackerPoint,
            int judgeFaces = DefaultJudgeDiceFaces, int damageOnFail = -1, int damageAdjust = 0, bool immune = false, int maxDamage = 0,
            int minPoint = 0)
        {
            if (judgeFaces <= 0) judgeFaces = DefaultJudgeDiceFaces;
            if (minPoint < 1) minPoint = 1;
            if (minPoint > judgeFaces) minPoint = judgeFaces;
            if (damageOnFail < 0) damageOnFail = Math.Max(1, finalAttack); // 默认: 失败承受全额攻击(未防御)

            int diceSpan = judgeFaces - minPoint + 1;
            // 闪避成功的点数个数: 一般需掷 > attackerPoint; 当 attackerPoint 达到上限时需 = 上限。
            int successFaces;
            if (attackerPoint >= judgeFaces)
                successFaces = 1;                       // 只有掷出上限本身才算(">="上限)
            else
                successFaces = judgeFaces - Math.Max(attackerPoint, minPoint - 1); // 掷出 (point, faces] 的点数
            if (successFaces < 0) successFaces = 0;
            if (successFaces > diceSpan) successFaces = diceSpan;

            double success = (double)successFaces / diceSpan;
            double fail = 1.0 - success;

            int dmgFail = Math.Max(1, damageOnFail);
            dmgFail += damageAdjust;                    // buff 易伤/减伤修正(闪避失败时才承受)
            if (dmgFail < 0) dmgFail = 0;
            if (maxDamage > 0 && dmgFail > maxDamage) dmgFail = maxDamage; // 单次受伤上限
            if (immune) dmgFail = 0;                     // 护盾/免疫
            bool knockOnFail = defenderHp > 0 && dmgFail >= defenderHp;

            return new DodgeResult
            {
                SuccessProb = success,
                KnockdownProb = knockOnFail ? fail : 0.0,
                ExpectedDamage = fail * dmgFail
            };
        }

        // =====================================================================
        // 攻击(我方攻击目标: 期望伤害 + 击杀率)
        // =====================================================================

        public struct AttackResult
        {
            /// <summary>期望造成的伤害。</summary>
            public double ExpectedDamage;
            /// <summary>击杀(伤害 >= 目标血量)的概率, 0..1。</summary>
            public double KillProb;
            public int MinDamage;
            public int MaxDamage;
        }

        /// <summary>
        /// 我方攻击结算。攻方骰未定时给出 attackDice; 已锁定时用 attackFlat 且 attackDice 传 null。
        /// 目标防御同理(defenseFlat + defenseDice)。targetHp = 目标当前血量。
        /// 对攻/防两侧骰池做联合卷积求 max(1, atk − def) 的分布。
        /// maxDamage 为单次受伤上限(0 = 无上限)。
        /// </summary>
        public static AttackResult Attack(int attackFlat, IReadOnlyList<int> attackDice,
            int defenseFlat, IReadOnlyList<int> defenseDice, int targetHp, int minChip = 1, int damageAdjust = 0, bool immune = false, int maxDamage = 0)
        {
            int atkMin;
            var atk = Distribution(attackFlat, attackDice, out atkMin);
            int defMin;
            var def = Distribution(defenseFlat, defenseDice, out defMin);

            double expDmg = 0.0, kill = 0.0;
            int minDmg = int.MaxValue, maxDmg = int.MinValue;
            for (int a = 0; a < atk.Length; a++)
            {
                double pa = atk[a];
                if (pa == 0.0) continue;
                int atkVal = atkMin + a;
                for (int d = 0; d < def.Length; d++)
                {
                    double pd = def[d];
                    if (pd == 0.0) continue;
                    int defVal = defMin + d;
                    int dmg = atkVal - defVal;
                    if (dmg < minChip) dmg = minChip; // 普通战斗命中保底
                    if (dmg < 0) dmg = 0;
                    dmg += damageAdjust;                 // buff 易伤/减伤修正(目标身上)
                    if (dmg < 0) dmg = 0;
                    if (maxDamage > 0 && dmg > maxDamage) dmg = maxDamage; // 单次受伤上限
                    if (immune) dmg = 0;                 // 目标护盾/免疫
                    double p = pa * pd;
                    expDmg += p * dmg;
                    if (dmg < minDmg) minDmg = dmg;
                    if (dmg > maxDmg) maxDmg = dmg;
                    if (targetHp > 0 && dmg >= targetHp) kill += p;
                }
            }
            if (minDmg == int.MaxValue) { minDmg = 0; maxDmg = 0; }

            return new AttackResult
            {
                ExpectedDamage = expDmg,
                KillProb = kill,
                MinDamage = minDmg,
                MaxDamage = maxDmg
            };
        }

        // =====================================================================
        // 骰池反推(仅有 Min/Max 时的近似 —— 精度低, 尽量避免)
        // =====================================================================

        /// <summary>
        /// 当只知道总值区间 [min,max] 而不知每颗骰子面数时, 用"单颗等效骰"做均匀近似:
        /// flat=min-1?  这里返回一颗 (max-min+1) 面骰 + flat=min-1 的等效, 使总和落在 [min,max] 且均匀。
        /// 明确标注为近似, 期望伤害会偏离真实(真实是钟形分布)。
        /// </summary>
        public static void EstimateFromRange(int min, int max, out int flat, out int[] dice)
        {
            if (max < min) { int t = min; min = max; max = t; }
            int span = max - min + 1;
            if (span <= 1) { flat = min; dice = new int[0]; return; }
            flat = min - 1;
            dice = new int[] { span };
        }

        /// <summary>
        /// 范围随机整数 + 一颗判定骰。范围内整数暂按等概率；两项独立。
        /// randomTerms 是卷积输入，范围项只是数学上的等效均匀项，不是玩家额外投出的骰子。
        /// knownPoint 指定时仅固定判定骰；最终攻击已锁定时应直接使用最终值，不再调用本方法。
        /// </summary>
        public static void RangeWithJudgeDice(int rangeMin, int rangeMax, out int flat,
            out int[] randomTerms, int judgeFaces = DefaultJudgeDiceFaces, int? knownPoint = null)
        {
            if (rangeMax < rangeMin) { int t = rangeMin; rangeMin = rangeMax; rangeMax = t; }
            if (judgeFaces <= 0) judgeFaces = DefaultJudgeDiceFaces;
            int width = rangeMax - rangeMin + 1;
            if (width == 1)
            {
                flat = rangeMin + (knownPoint.HasValue ? knownPoint.Value : 0);
                randomTerms = knownPoint.HasValue ? new int[0] : new[] { judgeFaces };
                return;
            }
            flat = rangeMin - 1;
            if (knownPoint.HasValue)
            {
                flat += knownPoint.Value;
                randomTerms = new[] { width };
            }
            else randomTerms = new[] { width, judgeFaces };
        }

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
        //   · 语境限定类(精准打击只吃【轨道轰炸】、渊蚀印记只吃魔渊触须)【不进】普通投牌结算,
        //     标 GeneralCombat=false, 只作展示提示, 不改数值。

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
        public static bool AppliesInContext(BuffDamageEffect effect, DamageContext context, long causeId = 0)
        {
            if (effect == null || effect.GeneralCombat) return true;
            if (effect.BuffId == 1221202) return context == DamageContext.Skill && causeId == 12203;
            if (effect.BuffId == 1291202)
                return context == DamageContext.Summon && (causeId == 12911 || causeId == 12912);
            return false;
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
            /// <summary>true=按层数(Progress)累乘; false=一次性(与层数无关)。</summary>
            public bool PerLayer;
            /// <summary>true=作用于普通投牌战斗; false=语境限定(只对特定来源伤害), 仅展示不计入。</summary>
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

            // —— 语境限定(不进普通投牌结算, 只展示) ——
            Add(new BuffDamageEffect(1221202, "精准打击", +1, false, false, false, "只对【轨道轰炸】伤害+1"));
            Add(new BuffDamageEffect(1291202, "渊蚀印记", +1, false, false, false, "只对魔渊触须/赛克斯伤害+1"));

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

        public static BuffAdjustment EvaluateTargetBuffs(IEnumerable<TargetBuff> targetBuffs, Dictionary<int, BuffDamageEffect> table, DamageContext context, long causeId, bool targetIsMonster)
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
                if (!eff.GeneralCombat && !AppliesInContext(eff, context, causeId))
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
