using System;
using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>
    /// 战斗概率计算核心 —— <b>纯逻辑, 零游戏/SDK 依赖, 可完全离线单测</b>。
    ///
    /// 机制来源(反编译 party.model.BattleRole / UI.FightWindow / Global.bin 验证):
    ///  - 攻/防最终值 = 基础值(InitAtk/InitDef) + 各出牌骰子之和; 服务器把区间下发为 Min/Max。
    ///  - 防御方在攻方投骰后做"防御 / 闪避"二选一(FightLogic.ReadyFightChoice)。
    ///  - 防御: 受伤 = max(0, 最终攻击 − 防御总值); 防御总值 = 防御基础 + 防御骰之和。
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
        /// minChip = 最小保底伤害(命中时至少扣这么多): 11 局真实回放对拍显示, Atk&lt;=Def 时防守方
        ///   仍几乎必掉 1 血(65/82 为 1), 故默认 1; 传 0 可退回纯 max(0,Atk-Def)。
        /// </summary>
        public static DefendResult Defend(int finalAttack, int defenderHp, int defenseFlat, IReadOnlyList<int> defenseDice, int minChip = 1, int damageAdjust = 0, bool immune = false)
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
                if (dmg < minChip) dmg = (finalAttack > 0 ? minChip : 0); // 命中保底; 无攻击则 0
                if (dmg < 0) dmg = 0;
                dmg += damageAdjust;                 // buff 易伤/减伤修正
                if (dmg < 0) dmg = 0;
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
        /// (通常 = max(0, finalAttack − 防御基础); 由调用方按是否保留基础防御决定)。
        /// </summary>
        public static DodgeResult Dodge(int finalAttack, int defenderHp, int attackerPoint,
            int judgeFaces = DefaultJudgeDiceFaces, int damageOnFail = -1, int damageAdjust = 0, bool immune = false)
        {
            if (judgeFaces <= 0) judgeFaces = DefaultJudgeDiceFaces;
            if (damageOnFail < 0) damageOnFail = finalAttack; // 默认: 失败承受全额攻击(未防御)

            // 闪避成功的点数个数: 一般需掷 > attackerPoint; 当 attackerPoint 达到上限时需 = 上限。
            int successFaces;
            if (attackerPoint >= judgeFaces)
                successFaces = 1;                       // 只有掷出上限本身才算(">="上限)
            else
                successFaces = judgeFaces - attackerPoint; // 掷出 (point, faces] 的点数
            if (successFaces < 0) successFaces = 0;
            if (successFaces > judgeFaces) successFaces = judgeFaces;

            double success = (double)successFaces / judgeFaces;
            double fail = 1.0 - success;

            int dmgFail = damageOnFail < 0 ? 0 : damageOnFail;
            dmgFail += damageAdjust;                    // buff 易伤/减伤修正(闪避失败时才承受)
            if (dmgFail < 0) dmgFail = 0;
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
        /// 对攻/防两侧骰池做联合卷积求 max(0, atk − def) 的分布。
        /// </summary>
        public static AttackResult Attack(int attackFlat, IReadOnlyList<int> attackDice,
            int defenseFlat, IReadOnlyList<int> defenseDice, int targetHp, int minChip = 1, int damageAdjust = 0, bool immune = false)
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
                    if (dmg < minChip) dmg = (atkVal > 0 ? minChip : 0); // 命中保底(回放校准)
                    if (dmg < 0) dmg = 0;
                    dmg += damageAdjust;                 // buff 易伤/减伤修正(目标身上)
                    if (dmg < 0) dmg = 0;
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
        /// 返回 count 颗 d6 的骰面数组(每颗 6 面)。11 局回放实测: 攻/防/闪的随机项都是 d6(点数 1..6),
        /// 攻方投骰 Val(5038) 与防守方 Val(5040) 均在 1..6; (Def-InitDef) 92% 落在 1..6(单 d6),
        /// 少数 7..12 为叠加第二颗 d6。因此战斗随机项应建模为 InitDef/base + N×d6, 而非区间均匀近似。
        /// </summary>
        public static int[] DiceD6(int count)
        {
            if (count < 0) count = 0;
            var a = new int[count];
            for (int i = 0; i < count; i++) a[i] = 6;
            return a;
        }

        /// <summary>
        /// 由基础值与总值区间推断"基础 + N×d6"模型。initFlat=已知确定基础(InitDef/攻方当前基础);
        /// max=可达上限。N = ceil((max-initFlat)/6), 至少 1(游戏默认必掷 1 颗 d6); 无区间信息时 N=1。
        /// 返回 flat=initFlat, dice=N×d6。这是 d6 模型下的精确骰池(非近似)。
        /// </summary>
        public static void D6ModelFromRange(int initFlat, int max, out int flat, out int[] dice)
        {
            flat = initFlat;
            int rollMax = max - initFlat;               // 掷骰可达的最大加成
            int n = rollMax <= 6 ? 1 : (rollMax + 5) / 6; // ceil(rollMax/6), 下限 1
            if (n < 1) n = 1;
            if (n > 4) n = 4;                            // 防御性上限
            dice = DiceD6(n);
        }

        // =====================================================================
        // Buff 伤害修正 —— 实时可读, 数据来源 Buff.bin/STRBuff 反编译
        // =====================================================================
        //
        // 针对【目标身上实时可读的 buff】: 服务器把每个单位的激活 buff 放在
        //   Hero.Buffs(MapField<long,Buff>), 每条含 BuffId + Progress(层数)。mod 通过
        //   Players.BuffsOf(unitId) 读到后, 用下表把"受到伤害±N"类效果换算成对该单位这一击的伤害修正。
        //
        // 关键结论(反编译 465 条 Buff 说明, buffcat 工具逐条提取):
        //   · "受到伤害/受到的伤害 ±N" 只有下表这些 buff 携带; 攻击力加成(如狂暴+3攻、逆鳞+2攻)
        //     一般已并入服务器下发的 BattleRole.Atk, 故本表【只取受伤侧的 ±N】, 不重复加攻。
        //   · 标记(10006)= 每层受伤+1, 玩家与怪物通用, 是最常见、用户点名的"标记—受到伤害+1"。
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

        /// <summary>梅加斯轨道轰炸：每丢弃两张牌触发一次，每3点总费用使每次伤害+1。</summary>
        public static int MegasOrbitalBombardmentCount(int discardedCards)
        {
            return discardedCards > 0 ? discardedCards / 2 : 0;
        }

        public static int MegasOrbitalBombardmentDamage(int discardedCards, int discardedCost)
        {
            if (MegasOrbitalBombardmentCount(discardedCards) == 0) return 0;
            if (discardedCost < 0) discardedCost = 0;
            return 3 + discardedCost / 3;
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
            public string Note;
            public BuffDamageEffect(int id, string name, int perUnit, bool perLayer, bool general, bool immunity, string note)
            { BuffId = id; Name = name; PerUnit = perUnit; PerLayer = perLayer; GeneralCombat = general; Immunity = immunity; Note = note; }
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
            Add(new BuffDamageEffect(10006,   "标记",     +1, true,  true,  false, "每层受到伤害+1(玩家/怪物通用)"));
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
            Add(new BuffDamageEffect(10331101,"深度改造", 0, false, true,  true,  "受到伤害降为0"));

            // —— 语境限定(不进普通投牌结算, 只展示) ——
            Add(new BuffDamageEffect(1221202, "精准打击", +1, false, false, false, "只对【轨道轰炸】伤害+1"));
            Add(new BuffDamageEffect(1291202, "渊蚀印记", +1, false, false, false, "只对魔渊触须/赛克斯伤害+1"));

            return t;
        }

        /// <summary>目标身上一条 buff 的实例(id + 层数)。用于喂给 <see cref="EvaluateTargetBuffs"/>。</summary>
        public struct TargetBuff
        {
            public int BuffId;
            public int Layers;
            public TargetBuff(int id, int layers) { BuffId = id; Layers = layers; }
        }

        /// <summary>对目标 buff 求值后的伤害修正。</summary>
        public struct BuffAdjustment
        {
            /// <summary>普通投牌战斗下, 该目标这一击的净受伤增量(正=易伤, 负=减伤)。</summary>
            public int Delta;
            /// <summary>是否免疫这一击(伤害归零)。</summary>
            public bool Immune;
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
                Applied = new List<string>(),
                ContextOnly = new List<string>(),
            };
            if (targetBuffs == null || table == null) return adj;

            foreach (var tb in targetBuffs)
            {
                if (!table.TryGetValue(tb.BuffId, out var eff) || eff == null) continue;
                int layers = tb.Layers > 0 ? tb.Layers : 1;

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
