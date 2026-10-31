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
    public static partial class CombatMath
    {
        /// <summary>游戏判定骰面数(Global.GAME_JUDGE_DICE_LIMIT), 闪避判定用。</summary>
        public const int DefaultJudgeDiceFaces = 6;

        /// <summary>摩西(枪匠)的角色 id。</summary>
        public const int MosesHeroId = 125;

        /// <summary>摩西 PVE 被动【精密技巧】的 buff id。</summary>
        public const int PrecisionBuffId = 1251201;

        /// <summary>【精密技巧】最高层数。</summary>
        public const int MaxPrecisionStacks = 3;

        /// <summary>赛克斯(魔渊幼体)的角色 id，【渊蚀印记】的伤害来源之一。</summary>
        public const int SeaxHeroId = 129;

        /// <summary>【渊蚀印记】的 buff id。</summary>
        public const int AbyssMarkBuffId = 1291202;

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
        // 代表值(界面显示口径)
        // =====================================================================

        /// <summary>
        /// 从"伤害值 → 概率"分布里取一个最贴近实际的整数伤害, 供界面显示:
        /// 有唯一众数时取众数; 并列众数或无众数(各值等概率)时取中位数,
        /// 中位数落在两个整数之间时四舍五入。这样不会显示平均值的小数。
        /// </summary>
        public static int TypicalDamageOf(IReadOnlyDictionary<int, double> distribution)
        {
            if (distribution == null || distribution.Count == 0) return 0;

            var values = new List<int>(distribution.Count);
            var probs = new List<double>(distribution.Count);
            foreach (var pair in distribution)
            {
                if (pair.Value <= 0.0) continue;
                values.Add(pair.Key);
                probs.Add(pair.Value);
            }
            if (values.Count == 0) return 0;

            // 1) 唯一众数: 概率严格最高的那个伤害值。
            double best = -1.0;
            int bestIndex = -1;
            bool tied = false;
            for (int i = 0; i < probs.Count; i++)
            {
                double p = probs[i];
                if (p > best + 1e-12)
                {
                    best = p;
                    bestIndex = i;
                    tied = false;
                }
                else if (Math.Abs(p - best) <= 1e-12)
                {
                    tied = true;
                }
            }
            if (!tied && bestIndex >= 0) return values[bestIndex];

            // 2) 没有唯一众数 → 中位数。按伤害值排序后累计概率;
            //    累计恰好到 50% 时, 中位数落在当前值与下一个值中间, 取中点再四舍五入。
            var order = new int[values.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, delegate (int a, int b) { return values[a].CompareTo(values[b]); });

            double cumulative = 0.0;
            for (int k = 0; k < order.Length; k++)
            {
                int i = order[k];
                cumulative += probs[i];
                if (cumulative > 0.5 + 1e-9) return values[i];
                if (Math.Abs(cumulative - 0.5) <= 1e-9)
                {
                    if (k + 1 < order.Length)
                        return (int)Math.Round((values[i] + values[order[k + 1]]) / 2.0, MidpointRounding.AwayFromZero);
                    return values[i];
                }
            }
            return values[order[order.Length - 1]];
        }

        // =====================================================================
        // 防御
        // =====================================================================

        public struct DefendResult
        {
            /// <summary>期望受到的伤害(概率加权平均, 内部参考值)。</summary>
            public double ExpectedDamage;
            /// <summary>界面上代表"这一下大概掉多少血"的整数伤害(众数/中位数)。</summary>
            public int TypicalDamage;
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
            var damageDist = new Dictionary<int, double>();
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
                damageDist[dmg] = (damageDist.TryGetValue(dmg, out double acc) ? acc : 0.0) + p;
                if (dmg < minDmg) minDmg = dmg;
                if (dmg > maxDmg) maxDmg = dmg;
                if (defenderHp > 0 && dmg >= defenderHp) knock += p;
            }
            if (minDmg == int.MaxValue) { minDmg = 0; maxDmg = 0; }

            return new DefendResult
            {
                ExpectedDamage = expDmg,
                TypicalDamage = TypicalDamageOf(damageDist),
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
            /// <summary>期望受到的伤害(概率加权平均, 内部参考值)。</summary>
            public double ExpectedDamage;
            /// <summary>界面上代表"这一下大概掉多少血"的整数伤害(众数/中位数)。</summary>
            public int TypicalDamage;
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

            var damageDist = new Dictionary<int, double>();
            if (success > 0.0) damageDist[0] = success;
            if (fail > 0.0)
                damageDist[dmgFail] = (damageDist.TryGetValue(dmgFail, out double acc) ? acc : 0.0) + fail;

            return new DodgeResult
            {
                SuccessProb = success,
                KnockdownProb = knockOnFail ? fail : 0.0,
                ExpectedDamage = fail * dmgFail,
                TypicalDamage = TypicalDamageOf(damageDist)
            };
        }

        // =====================================================================
        // 攻击(我方攻击目标: 预计伤害 + 击杀率)
        // =====================================================================

        public struct AttackResult
        {
            /// <summary>期望造成的伤害(概率加权平均, 内部参考值)。</summary>
            public double ExpectedDamage;
            /// <summary>界面上代表"这一下大概打多少"的整数伤害(众数/中位数)。</summary>
            public int TypicalDamage;
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
            var damageDist = new Dictionary<int, double>();
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
                    damageDist[dmg] = (damageDist.TryGetValue(dmg, out double acc) ? acc : 0.0) + p;
                    if (dmg < minDmg) minDmg = dmg;
                    if (dmg > maxDmg) maxDmg = dmg;
                    if (targetHp > 0 && dmg >= targetHp) kill += p;
                }
            }
            if (minDmg == int.MaxValue) { minDmg = 0; maxDmg = 0; }

            return new AttackResult
            {
                ExpectedDamage = expDmg,
                TypicalDamage = TypicalDamageOf(damageDist),
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

    }
}
