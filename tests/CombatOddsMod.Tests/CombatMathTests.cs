using System;
using Xunit;
using CombatOddsMod;

namespace CombatOddsMod.Tests
{
    public class CombatMathTests
    {
        private const double Eps = 1e-9;

        // ============================ 骰池分布 ============================

        [Fact]
        public void Distribution_NoDice_IsDeterministicFlat()
        {
            int min;
            var d = CombatMath.Distribution(5, null, out min);
            Assert.Equal(5, min);
            Assert.Single(d);
            Assert.Equal(1.0, d[0], 12);
        }

        [Fact]
        public void Distribution_SingleD6_IsUniform()
        {
            int min;
            var d = CombatMath.Distribution(0, new[] { 6 }, out min);
            Assert.Equal(1, min);            // 最小 1
            Assert.Equal(6, d.Length);       // 1..6
            foreach (var p in d) Assert.Equal(1.0 / 6.0, p, 12);
            Assert.Equal(1.0, Sum(d), 12);
        }

        [Fact]
        public void Distribution_D6PlusD10_IsConvolution_NotUniform()
        {
            int min;
            var d = CombatMath.Distribution(0, new[] { 6, 10 }, out min);
            Assert.Equal(2, min);            // 1+1
            Assert.Equal(15, d.Length);      // 2..16
            Assert.Equal(1.0, Sum(d), 12);
            // 和=2 只有 (1,1) 一种 => 1/60
            Assert.Equal(1.0 / 60.0, d[0], 12);
            // 和=4 => (1,3)(2,2)(3,1) = 3/60
            Assert.Equal(3.0 / 60.0, d[2], 12);
        }

        // ============================ c204 的实例(红绿校验) ============================
        //
        // 场景: 怪 13 攻, 你防 3+1~6+1~10, 怪扔 3(闪避阈值), 你 6 血。该防该闪?
        //
        //  - 防御: 受伤 = max(0, 13 − 防御); 防御 = 3 + d6 + d10 (区间 5..19)。
        //          被击倒需 受伤>=6 => 防御<=7 => d6+d10<=4 => 6/60 = 10%。
        //  - 闪避: d6 需 >3 才成功 => {4,5,6} = 3/6 = 50% 成功; 失败承受全额 13 => 必被击倒 => 50%。
        //  => 防御被击倒 10% ≪ 闪避被击倒 50%, 应当【防御】。
        //     (c204 口算防御 15% 是人为估计, 精确卷积是 10%; 闪避 50% 与其口算一致。)

        [Fact]
        public void C204Example_Defend_Knockdown_Is10Percent()
        {
            var r = CombatMath.Defend(finalAttack: 13, defenderHp: 6, defenseFlat: 3, defenseDice: new[] { 6, 10 }, minChip: 1);
            Assert.Equal(0.10, r.KnockdownProb, 6);       // 保底伤害 1 < 6, 不改被击倒率
            Assert.Equal(1, r.MinDamage);                 // 防御 19 时 13-19<0, 命中保底 => 1(回放校准)
            Assert.Equal(8, r.MaxDamage);                 // 防御 5 时 13-5 = 8
            Assert.False(r.Approx);
            // 期望受伤 = E[max(1,13-def)], def=3+d6+d10。
            Assert.Equal(ExpectedDefendDamage(), r.ExpectedDamage, 6);
        }

        [Fact]
        public void C204Example_Defend_PureClamp_NoChip_Is10PercentAndZeroFloor()
        {
            // 传 minChip:0 退回纯 max(0,Atk-Def), 便于与"无保底"口径对照。
            var r = CombatMath.Defend(13, 6, 3, new[] { 6, 10 }, minChip: 0);
            Assert.Equal(0.10, r.KnockdownProb, 6);
            Assert.Equal(0, r.MinDamage);
        }

        [Fact]
        public void C204Example_Dodge_Knockdown_Is50Percent()
        {
            var r = CombatMath.Dodge(finalAttack: 13, defenderHp: 6, attackerPoint: 3);
            Assert.Equal(0.5, r.SuccessProb, 12);
            Assert.Equal(0.5, r.KnockdownProb, 12);
            Assert.Equal(0.5 * 13, r.ExpectedDamage, 12);
        }

        [Fact]
        public void C204Example_DefendIsSaferThanDodge()
        {
            var def = CombatMath.Defend(13, 6, 3, new[] { 6, 10 });
            var dodge = CombatMath.Dodge(13, 6, 3);
            Assert.True(def.KnockdownProb < dodge.KnockdownProb);
        }

        // ============================ 均匀整数区间 + 单颗判定骰 ============================

        [Fact]
        public void RangeWithJudgeDice_WideRange_IsConvolutionWithSingleD6()
        {
            int flat; int[] randomTerms; int min;
            CombatMath.RangeWithJudgeDice(2, 23, out flat, out randomTerms);
            var d = CombatMath.Distribution(flat, randomTerms, out min);
            Assert.Equal(3, min);
            Assert.Equal(29, min + d.Length - 1);
            Assert.Equal(1.0, Sum(d), 12);
            Assert.Equal(1.0 / 132.0, d[0], 12);
            Assert.Equal(1.0 / 132.0, d[d.Length - 1], 12);
            Assert.Equal(6.0 / 132.0, d[5], 12);
        }

        [Fact]
        public void RangeWithJudgeDice_KnownPoint_OnlyRangeRemainsRandom()
        {
            int flat; int[] randomTerms; int min;
            CombatMath.RangeWithJudgeDice(2, 23, out flat, out randomTerms, knownPoint: 4);
            var d = CombatMath.Distribution(flat, randomTerms, out min);
            Assert.Equal(6, min);
            Assert.Equal(27, min + d.Length - 1);
            Assert.Equal(1.0, Sum(d), 12);
            foreach (var p in d) Assert.Equal(1.0 / 22.0, p, 12);
        }

        [Fact]
        public void RangeWithJudgeDice_FixedRange_HasOnlyOneJudgeDie()
        {
            int flat; int[] randomTerms; int min;
            CombatMath.RangeWithJudgeDice(5, 5, out flat, out randomTerms);
            Assert.Equal(5, flat);
            Assert.Equal(new[] { 6 }, randomTerms);
            var d = CombatMath.Distribution(flat, randomTerms, out min);
            Assert.Equal(6, min);
            Assert.Equal(6, d.Length);
            foreach (var p in d) Assert.Equal(1.0 / 6.0, p, 12);
        }

        [Theory]
        [InlineData(2, 23, 3, 8, 6)]
        [InlineData(4, 4, 2, 2, 3)]
        [InlineData(-2, 5, 0, 3, 4)]
        public void RangeBattle_AttackAndDefend_MatchIndependentFourWayEnumeration(
            int attackMin, int attackMax, int defenseMin, int defenseMax, int defenderHp)
        {
            int attackFlat; int[] attackTerms; int defenseFlat; int[] defenseTerms;
            CombatMath.RangeWithJudgeDice(attackMin, attackMax, out attackFlat, out attackTerms);
            CombatMath.RangeWithJudgeDice(defenseMin, defenseMax, out defenseFlat, out defenseTerms);
            var attack = CombatMath.Attack(attackFlat, attackTerms, defenseFlat, defenseTerms,
                defenderHp, minChip: 0);

            // 独立枚举区间整数和两颗判定骰，不使用 Distribution 推导期望值。
            double totalDamage = 0, totalKills = 0, defendDamage = 0, defendKnockdowns = 0;
            int outcomes = 0, minDamage = int.MaxValue, maxDamage = int.MinValue;
            for (int a = attackMin; a <= attackMax; a++)
                for (int ap = 1; ap <= 6; ap++)
                {
                    int finalAttack = a + ap;
                    var defend = CombatMath.Defend(finalAttack, defenderHp, defenseFlat, defenseTerms, minChip: 0);
                    double lockedDamage = 0, lockedKills = 0;
                    int defenseOutcomes = 0;
                    for (int d = defenseMin; d <= defenseMax; d++)
                        for (int dp = 1; dp <= 6; dp++)
                        {
                            int finalDefense = d + dp;
                            int damage = Math.Max(0, finalAttack - finalDefense);
                            bool knockedDown = !(finalAttack < defenderHp + finalDefense);
                            totalDamage += damage;
                            lockedDamage += damage;
                            if (knockedDown) { totalKills++; lockedKills++; }
                            minDamage = Math.Min(minDamage, damage);
                            maxDamage = Math.Max(maxDamage, damage);
                            outcomes++;
                            defenseOutcomes++;
                        }
                    Assert.Equal(lockedDamage / defenseOutcomes, defend.ExpectedDamage, 12);
                    Assert.Equal(lockedKills / defenseOutcomes, defend.KnockdownProb, 12);
                    Assert.False(defend.Approx);
                    defendDamage += defend.ExpectedDamage;
                    defendKnockdowns += defend.KnockdownProb;
                }
            int attackOutcomes = (attackMax - attackMin + 1) * 6;
            Assert.Equal(totalDamage / outcomes, attack.ExpectedDamage, 12);
            Assert.Equal(totalKills / outcomes, attack.KillProb, 12);
            Assert.Equal(minDamage, attack.MinDamage);
            Assert.Equal(maxDamage, attack.MaxDamage);
            Assert.Equal(totalDamage / outcomes, defendDamage / attackOutcomes, 12);
            Assert.Equal(totalKills / outcomes, defendKnockdowns / attackOutcomes, 12);
        }

        [Theory]
        [InlineData(8, 0.0)]
        [InlineData(9, 1.0)]
        [InlineData(10, 1.0)]
        public void Battle_EqualityAtHpPlusDefense_IsKnockdown(int finalAttack, double expectedKnockdown)
        {
            // HP=6、防御=3，只有 A<9 才存活；A=9 已击倒。
            var attack = CombatMath.Attack(finalAttack, null, 3, null, targetHp: 6, minChip: 0);
            var defend = CombatMath.Defend(finalAttack, 6, 3, null, minChip: 0);
            Assert.Equal(expectedKnockdown, attack.KillProb, 12);
            Assert.Equal(expectedKnockdown, defend.KnockdownProb, 12);
        }

        // ============================ 闪避边界 ============================
        [Fact]
        public void Dodge_ThresholdBelowMax_SuccessIsComplement()
        {
            // 阈值 1: 需 >1 => {2,3,4,5,6} = 5/6
            Assert.Equal(5.0 / 6.0, CombatMath.Dodge(10, 3, 1).SuccessProb, 12);
            // 阈值 5: 需 >5 => {6} = 1/6
            Assert.Equal(1.0 / 6.0, CombatMath.Dodge(10, 3, 5).SuccessProb, 12);
        }

        [Fact]
        public void Dodge_ThresholdAtMax_NeedsExactMax()
        {
            // 攻方投出上限 6: 需掷 = 6 => 1/6 (UI 里显示 "=6")
            Assert.Equal(1.0 / 6.0, CombatMath.Dodge(10, 3, 6).SuccessProb, 12);
        }

        [Fact]
        public void Dodge_FailDamageBelowHp_NoKnockdown()
        {
            // 失败只受 2 点, 血 6 => 不会被击倒, 但仍有期望伤害。
            var r = CombatMath.Dodge(finalAttack: 20, defenderHp: 6, attackerPoint: 3, damageOnFail: 2);
            Assert.Equal(0.0, r.KnockdownProb, 12);
            Assert.Equal(0.5 * 2, r.ExpectedDamage, 12);
        }

        // ============================ 防御:确定值 ============================

        [Fact]
        public void Defend_NoDice_Deterministic()
        {
            // 防御固定 10, 攻 13 => 受伤 3, 血 6 => 不倒。
            var r = CombatMath.Defend(13, 6, 10, null);
            Assert.Equal(3.0, r.ExpectedDamage, 12);
            Assert.Equal(0.0, r.KnockdownProb, 12);
            Assert.Equal(3, r.MinDamage);
            Assert.Equal(3, r.MaxDamage);
        }

        [Fact]
        public void Defend_MinChipDamage_CalibratedToReplays()
        {
            // 显式启用旧保底模型：Atk<=Def 时仍受1点伤害；默认模型无保底。
            var r = CombatMath.Defend(3, 6, 10, null, minChip: 1);   // 防 10 > 攻 3
            Assert.Equal(1.0, r.ExpectedDamage, 12);
            Assert.Equal(1, r.MinDamage);
            Assert.Equal(1, r.MaxDamage);
            // 显式 minChip:0 时无保底伤害。
            var r0 = CombatMath.Defend(3, 6, 10, null, minChip: 0);
            Assert.Equal(0.0, r0.ExpectedDamage, 12);
            Assert.Equal(0, r0.MaxDamage);
        }

        [Fact]
        public void AttackAndDefend_DefaultNoChip_DefenseAboveAttackPreservesOneHp()
        {
            var attack = CombatMath.Attack(3, null, 10, null, targetHp: 1);
            var defend = CombatMath.Defend(3, 1, 10, null);
            Assert.Equal(0.0, attack.ExpectedDamage, 12);
            Assert.Equal(0.0, attack.KillProb, 12);
            Assert.Equal(0, attack.MinDamage);
            Assert.Equal(0, attack.MaxDamage);
            Assert.Equal(0.0, defend.ExpectedDamage, 12);
            Assert.Equal(0.0, defend.KnockdownProb, 12);
            Assert.Equal(0, defend.MinDamage);
            Assert.Equal(0, defend.MaxDamage);
        }

        // ============================ 攻击:期望伤害 + 击杀率 ============================

        [Fact]
        public void Attack_FixedVsFixed_KillWhenLethal()
        {
            // 我攻固定 10, 目标防固定 2, 目标血 5 => 伤害 8 >= 5 => 必杀。
            var r = CombatMath.Attack(10, null, 2, null, targetHp: 5);
            Assert.Equal(8.0, r.ExpectedDamage, 12);
            Assert.Equal(1.0, r.KillProb, 12);
        }

        [Fact]
        public void Attack_WithDice_KillProbBetweenZeroAndOne()
        {
            // 我攻 = 5 + d6 (6..11), 目标防固定 4, 目标血 5 => 伤害 = max(0, 攻-4) = 2..7。
            // 击杀需 伤害>=5 => 攻>=9 => 5+d6>=9 => d6>=4 => {4,5,6} = 3/6 = 0.5。
            var r = CombatMath.Attack(5, new[] { 6 }, 4, null, targetHp: 5);
            Assert.Equal(0.5, r.KillProb, 12);
        }

        [Fact]
        public void Attack_TotalProbabilityIsOne_OverJointDice()
        {
            // 联合分布应归一(通过击杀率+非击杀率=1 间接校验极端阈值)。
            var rAll = CombatMath.Attack(100, new[] { 6 }, 0, new[] { 6 }, targetHp: 1); // 必杀
            Assert.Equal(1.0, rAll.KillProb, 12);
            var rNone = CombatMath.Attack(1, new[] { 6 }, 0, new[] { 6 }, targetHp: 1000); // 必不杀
            Assert.Equal(0.0, rNone.KillProb, 12);
        }

        // ============================ 区间近似 ============================

        [Fact]
        public void EstimateFromRange_ProducesUniformOverRange()
        {
            int flat; int[] dice;
            CombatMath.EstimateFromRange(5, 19, out flat, out dice);
            int min;
            var d = CombatMath.Distribution(flat, dice, out min);
            Assert.Equal(5, min);
            Assert.Equal(15, d.Length);          // 5..19
            Assert.Equal(1.0, Sum(d), 12);
            foreach (var p in d) Assert.Equal(1.0 / 15.0, p, 12); // 均匀(近似)
        }

        // ============================ Buff 伤害修正 ============================

        [Fact]
        public void BuffTable_HasMarkAndKuangbao()
        {
            var t = CombatMath.DefaultBuffTable();
            Assert.True(t.ContainsKey(10006), "应含标记 10006");
            Assert.True(t.ContainsKey(2000801), "应含卡牌狂暴 2000801");
            Assert.Equal(1, t[10006].PerUnit);
            Assert.True(t[10006].PerLayer);
            Assert.True(t[10006].GeneralCombat);
            Assert.Equal(1, t[2000801].PerUnit);
            Assert.True(t[2000801].PerLayer);
        }

        [Fact]
        public void EvaluateTargetBuffs_MarkStacks_PerLayer()
        {
            var t = CombatMath.DefaultBuffTable();
            var buffs = new[] { new CombatMath.TargetBuff(10006, 3) }; // 标记 3 层
            var adj = CombatMath.EvaluateTargetBuffs(buffs, t);
            Assert.Equal(3, adj.Delta);            // 每层 +1 × 3
            Assert.False(adj.Immune);
            Assert.Single(adj.Applied);
        }

        [Fact]
        public void EvaluateTargetBuffs_MarkPlusKuangbao_SumsDeltas()
        {
            var t = CombatMath.DefaultBuffTable();
            var buffs = new[]
            {
                new CombatMath.TargetBuff(10006, 2),   // 标记 x2 => +2
                new CombatMath.TargetBuff(2000801, 3), // 狂暴 x3 => +3
            };
            var adj = CombatMath.EvaluateTargetBuffs(buffs, t);
            Assert.Equal(5, adj.Delta);
        }

        [Fact]
        public void EvaluateTargetBuffs_Reduction_IsNegative()
        {
            var t = CombatMath.DefaultBuffTable();
            var buffs = new[] { new CombatMath.TargetBuff(10551101, 2) }; // 稳定中枢 每层 -1
            var adj = CombatMath.EvaluateTargetBuffs(buffs, t);
            Assert.Equal(-2, adj.Delta);
        }

        [Fact]
        public void EvaluateTargetBuffs_Immunity_SetsImmuneFlag()
        {
            var t = CombatMath.DefaultBuffTable();
            var buffs = new[] { new CombatMath.TargetBuff(1071101, 1) }; // 护盾: 下次-99
            var adj = CombatMath.EvaluateTargetBuffs(buffs, t);
            Assert.True(adj.Immune);
        }

        [Fact]
        public void EvaluateTargetBuffs_ContextOnly_NotCounted()
        {
            var t = CombatMath.DefaultBuffTable();
            var buffs = new[] { new CombatMath.TargetBuff(1291202, 5) }; // 渊蚀印记: 只吃魔渊触须/赛克斯
            var adj = CombatMath.EvaluateTargetBuffs(buffs, t);
            Assert.Equal(0, adj.Delta);
            Assert.False(adj.Immune);
            Assert.Single(adj.ContextOnly);
        }

        [Fact]
        public void EvaluateTargetBuffs_ContextOnly_AppliesToSummonContext()
        {
            var t = CombatMath.DefaultBuffTable();
            var buffs = new[] { new CombatMath.TargetBuff(1291202, 1) };
            var normal = CombatMath.EvaluateTargetBuffs(buffs, t, CombatMath.DamageContext.NormalBattle);
            var summon = CombatMath.EvaluateTargetBuffs(buffs, t, CombatMath.DamageContext.Summon, 12911);
            Assert.Equal(0, normal.Delta);
            Assert.Single(normal.ContextOnly);
            Assert.Equal(1, summon.Delta);
            Assert.Empty(summon.ContextOnly);
        }

        [Fact]
        public void DamageTypeMapsToDamageContext()
        {
            Assert.Equal(CombatMath.DamageContext.Skill, CombatMath.ContextFromDamageType(1));
            Assert.Equal(CombatMath.DamageContext.Summon, CombatMath.ContextFromDamageType(9));
            Assert.Equal(CombatMath.DamageContext.NormalBattle, CombatMath.ContextFromDamageType(7));
        }
        [Fact]
        public void PrecisionStrike_AppliesOnlyToMegasOrbitalBombardment()
        {
            var t = CombatMath.DefaultBuffTable();
            var buffs = new[] { new CombatMath.TargetBuff(1221202, 1) };
            var ordinarySkill = CombatMath.EvaluateTargetBuffs(buffs, t, CombatMath.DamageContext.Skill, 103701);
            var orbitalBombardment = CombatMath.EvaluateTargetBuffs(buffs, t, CombatMath.DamageContext.Skill, 12203);
            Assert.Equal(0, ordinarySkill.Delta);
            Assert.Single(ordinarySkill.ContextOnly);
            Assert.Equal(1, orbitalBombardment.Delta);
            Assert.Empty(orbitalBombardment.ContextOnly);
        }

        [Fact]
        public void MegasBombardment_UsesDiscardCountAndTotalCost()
        {
            Assert.Equal(0, CombatMath.MegasOrbitalBombardmentCount(1));
            Assert.Equal(2, CombatMath.MegasOrbitalBombardmentCount(5));
            Assert.Equal(5, CombatMath.MegasOrbitalBombardmentDamage(4, 6));
            Assert.Equal(6, CombatMath.MegasOrbitalBombardmentDamage(6, 9));
        }

        [Fact]
        public void BonnieBonus_RequiresBonnieMarkedMonster()
        {
            Assert.Equal(3, CombatMath.BonnieMarkedMonsterAttackBonus(127, true, true));
            Assert.Equal(0, CombatMath.BonnieMarkedMonsterAttackBonus(127, true, false));
            Assert.Equal(0, CombatMath.BonnieMarkedMonsterAttackBonus(127, false, true));
            Assert.Equal(0, CombatMath.BonnieMarkedMonsterAttackBonus(129, true, true));
        }
        [Fact]
        public void BlueHaeGyeongWeakness_AppliesOnlyToMonsterTarget()
        {
            var buffs = new[] { new CombatMath.TargetBuff(1140102, 1) };
            var table = CombatMath.DefaultBuffTable();
            var monster = CombatMath.EvaluateTargetBuffs(buffs, table, CombatMath.DamageContext.NormalBattle, 0, true);
            var player = CombatMath.EvaluateTargetBuffs(buffs, table, CombatMath.DamageContext.NormalBattle, 0, false);
            Assert.Equal(1, monster.Delta);
            Assert.Single(monster.Applied);
            Assert.Equal(0, player.Delta);
            Assert.Single(player.ContextOnly);
        }
        [Fact]
        public void RequiredRangeBattleRolls_LockedAttack_UsesDefenderHpAndFinalAttack()
        {
            // 攻击区间 2..5，防御区间 3..8，防守方 HP=6。
            // 可能击杀需 5+r >= 6+3+1；保证击杀需 2+r >= 6+8+6。
            // 攻击已锁定17：可能存活需 17 < 6+8+r，保证存活需 17 < 6+3+r。
            var t = CombatMath.RequiredRangeBattleRolls(2, 5, 3, 8, 6, 17, 3, true);
            Assert.Equal(5, t.AttackRollToPossiblyKill);
            Assert.Equal(18, t.AttackRollToGuaranteeKill);
            Assert.Equal(4, t.DefenseRollToPossiblySurvive);
            Assert.Equal(9, t.DefenseRollToGuaranteeSurvive);
            Assert.Equal(4, t.DodgeRollToSucceed);
        }

        [Fact]
        public void RequiredRangeBattleRolls_UnthrownAttack_UsesBestAndWorstAttackExtremes()
        {
            // 未投攻击为 8..12+d6 => 9..18，finalAttack 占位值99不能参与计算。
            var t = CombatMath.RequiredRangeBattleRolls(8, 12, 2, 4, 3, 99, 0, false);
            Assert.Equal(1, t.AttackRollToPossiblyKill); // 所有合法骰点都有可能击杀，阈值下限为1。
            Assert.Equal(5, t.AttackRollToGuaranteeKill);
            Assert.Equal(3, t.DefenseRollToPossiblySurvive);
            Assert.Equal(14, t.DefenseRollToGuaranteeSurvive);
        }

        [Theory]
        [InlineData(-2, 7, 20, 2, 7)]
        [InlineData(0, 5, 18, 4, 9)]
        [InlineData(2, 3, 16, 6, 11)]
        public void RequiredRangeBattleRolls_DamageAdjustment_ShiftsStrictHpBoundaries(
            int damageAdjust, int possibleKill, int guaranteeKill, int possibleSurvive, int guaranteeSurvive)
        {
            var t = CombatMath.RequiredRangeBattleRolls(2, 5, 3, 8, 6, 17, 3, true, damageAdjust);
            Assert.Equal(possibleKill, t.AttackRollToPossiblyKill);
            Assert.Equal(guaranteeKill, t.AttackRollToGuaranteeKill);
            Assert.Equal(possibleSurvive, t.DefenseRollToPossiblySurvive);
            Assert.Equal(guaranteeSurvive, t.DefenseRollToGuaranteeSurvive);
        }

        [Fact]
        public void RequiredRangeBattleRolls_FixedRange_EqualityKillsAndNeedsOneMoreToSurvive()
        {
            // A=9、HP=6、基础防御2：防御掷1时总防御3，恰好归零；掷2才存活。
            var t = CombatMath.RequiredRangeBattleRolls(8, 8, 2, 2, 6, 9, 6, true);
            Assert.Equal(1, t.AttackRollToPossiblyKill);
            Assert.Equal(6, t.AttackRollToGuaranteeKill);
            Assert.Equal(2, t.DefenseRollToPossiblySurvive);
            Assert.Equal(2, t.DefenseRollToGuaranteeSurvive);
            Assert.Equal(6, t.DodgeRollToSucceed);
        }

        [Fact]
        public void RequiredRangeBattleRolls_CustomJudgeFaces_UsesSingleJudgeDie()
        {
            var t = CombatMath.RequiredRangeBattleRolls(2, 5, 3, 8, 6, 17, 10, true, judgeFaces: 10);
            Assert.Equal(5, t.AttackRollToPossiblyKill);
            Assert.Equal(22, t.AttackRollToGuaranteeKill);
            Assert.Equal(4, t.DefenseRollToPossiblySurvive);
            Assert.Equal(9, t.DefenseRollToGuaranteeSurvive);
            Assert.Equal(10, t.DodgeRollToSucceed);
        }
        [Fact]
        public void KnownSkillLabels_MatchObservedCharacterSkills()
        {
            Assert.Equal("梅加斯·轨道轰炸", CombatMath.KnownSkillLabel(12203));
            Assert.Equal("赛克斯·魔域转化", CombatMath.KnownSkillLabel(12902));
            Assert.Equal("蓝海晴·虚弱印记", CombatMath.KnownSkillLabel(11403));
            Assert.Equal("邦妮·隐匿行动", CombatMath.KnownSkillLabel(12702));
            Assert.Null(CombatMath.KnownSkillLabel(99999));
        }

        [Fact]
        public void EvaluateTargetBuffs_UnknownBuff_Ignored()
        {
            var t = CombatMath.DefaultBuffTable();
            var buffs = new[] { new CombatMath.TargetBuff(999999, 4) };
            var adj = CombatMath.EvaluateTargetBuffs(buffs, t);
            Assert.Equal(0, adj.Delta);
            Assert.Empty(adj.Applied);
            Assert.Empty(adj.ContextOnly);
        }

        [Fact]
        public void Defend_WithVulnerability_RaisesKnockdown()
        {
            // 攻13, 防基础3+1×d6, 血6。裸算 vs +2 易伤(标记x2)对比击倒率。
            var baseR = CombatMath.Defend(13, 6, 3, new[] { 6 });
            var buffed = CombatMath.Defend(13, 6, 3, new[] { 6 }, 1, 2, false);
            Assert.True(buffed.KnockdownProb >= baseR.KnockdownProb);
            Assert.True(buffed.ExpectedDamage > baseR.ExpectedDamage);
        }

        [Fact]
        public void Defend_Immune_ZeroDamageNoKnockdown()
        {
            var r = CombatMath.Defend(99, 1, 0, null, 1, 0, true);
            Assert.Equal(0.0, r.ExpectedDamage, 9);
            Assert.Equal(0.0, r.KnockdownProb, 9);
        }

        [Fact]
        public void Attack_WithVulnerability_RaisesKill()
        {
            // 我方攻锁定10, 目标防基础4+1×d6, 目标血5。+2 易伤应提高击杀率。
            var baseR = CombatMath.Attack(10, null, 4, new[] { 6 }, 5);
            var buffed = CombatMath.Attack(10, null, 4, new[] { 6 }, 5, 1, 2, false);
            Assert.True(buffed.KillProb >= baseR.KillProb);
            Assert.True(buffed.ExpectedDamage > baseR.ExpectedDamage);
        }

        [Fact]
        public void Attack_TargetImmune_NoKill()
        {
            var r = CombatMath.Attack(99, null, 0, null, 1, 1, 0, true);
            Assert.Equal(0.0, r.KillProb, 9);
            Assert.Equal(0.0, r.ExpectedDamage, 9);
        }

        [Fact]
        public void Dodge_WithVulnerability_RaisesKnockdownOnFail()
        {
            // 攻8, 血6, 攻方骰点3。闪避失败承受8; 裸算不致命, +? 不改成功率但影响失败伤害/击倒。
            var baseR = CombatMath.Dodge(8, 6, 3, 6, 5);          // 失败承受5, 5<6 不击倒
            var buffed = CombatMath.Dodge(8, 6, 3, 6, 5, 2, false); // 失败承受5+2=7>=6 击倒
            Assert.Equal(baseR.SuccessProb, buffed.SuccessProb, 9); // 易伤不改闪避成功率
            Assert.True(buffed.KnockdownProb > baseR.KnockdownProb);
        }

        [Fact]
        public void EvaluateTargetBuffs_NullSafe()
        {
            var adj = CombatMath.EvaluateTargetBuffs(null, null);
            Assert.Equal(0, adj.Delta);
            Assert.NotNull(adj.Applied);
            Assert.NotNull(adj.ContextOnly);
        }

        // ============================ 辅助 ============================

        private static double Sum(double[] a)
        {
            double s = 0; foreach (var x in a) s += x; return s;
        }

        // 精确期望防御伤害: def = 3 + d6 + d10, 受伤 = max(1, 13 - def)(命中保底 1, 回放校准)。
        private static double ExpectedDefendDamage()
        {
            double sum = 0;
            for (int a = 1; a <= 6; a++)
                for (int b = 1; b <= 10; b++)
                {
                    int def = 3 + a + b;
                    int dmg = Math.Max(1, 13 - def);   // finalAttack=13>0, 保底 1
                    sum += dmg;
                }
            return sum / 60.0;
        }
    }
}
