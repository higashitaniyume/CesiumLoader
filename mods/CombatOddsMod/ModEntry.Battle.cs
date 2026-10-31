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
        private static void OnBattlePhaseChanged(Battle b, BattlePhase phase)
        {
            // 攻方刚投骰: 投出 0 点时 Atk/Point 都不变, BattleUpdate 会被去重, 这里补一次重算。
            if (phase != BattlePhase.AttackerRolled) return;
            OnBattleUpdate(b);
        }

        private static void OnBattleUpdate(Battle b)
        {
            try
            {
                if (b == null || b.Attacker == null || b.Defender == null) return;
                if (b.IsEnd) { _lastBattle = null; Clear(); return; }

                _lastBattle = b;

                var atk = b.Attacker;
                var def = b.Defender;

                bool iAmDefender = Players.IsSelf(def.PlayerId);
                bool iAmAttacker = Players.IsSelf(atk.PlayerId);
                bool involved = iAmDefender || iAmAttacker;
                if (!involved && !_cfg.ShowForOthers) return;

                // 别人打怪/防御时也要有显示: 攻、守两个视角都放出来(观战)。
                //   · 我在场: 只显示我这一侧(攻或防)。
                //   · 别人的战斗(观战): 攻击方击杀率 + 防守方该防/该闪, 两块都给。
                bool showDefender = iAmDefender || (_cfg.ShowForOthers && !iAmAttacker);
                bool showAttacker = iAmAttacker || (_cfg.ShowForOthers && !iAmDefender);

                var sb = new StringBuilder();
                string atkName = SafeName(atk);
                string defName = SafeName(def);

                // 观战(非自己参战): 顶部一行小字标注谁打谁。
                if (!involved) sb.Append(Dim("👁 " + atkName + " ⚔ " + defName)).Append('\n');

                // 攻方骰点: 未投骰 = -1(此时 Point 固定为 0, 不代表点数), 已投骰 = 0..6。
                // 0 是有效点数(摩西【破绽】可把怪物战斗骰点压到 0), 不能用 Point > 0 判断是否投过。
                int atkPoint = GameEvents.AttackerPoint;
                int finalAtk = SafeInt(() => atk.Atk);
                int bonnieBonus = BonnieMarkedMonsterBonus(atk, def);
                finalAtk += bonnieBonus;
                bool attackerThrew = atkPoint >= 0;

                // 目标(防守方/被击中者)身上的实时 buff → 受伤修正(易伤/减伤/免疫)。
                // 攻、守两个视角命中的都是同一个"被击中者"= 防守方, 故只求一次。
                // 攻方 HeroId 一并传进去: 渊蚀印记只对赛克斯(129)打出的伤害生效。
                int attackerHeroId = HeroIdOf(atk);
                var badj = BuffAdjOf(def.PlayerId, CombatMath.DamageContext.NormalBattle, 0, IsMonster(def.PlayerId), attackerHeroId);
#if COMBATODDS_DEBUG_PROBES
                LogAshDiagnostic(def.PlayerId, badj);
#endif
                int dmgAdjust = badj.Delta;
                bool targetImmune = badj.Immune;
                int dmgCap = badj.MaxDamagePerHit;   // 单次受伤上限(0=无)

                // --------- 防守方视角: 一眼看该防还是该闪 ---------
                if (showDefender)
                {
                    int hp = HpOf(def.PlayerId);
                    int initDef = SafeInt(() => def.InitDef);
                    int maxDef = SafeInt(() => def.MaxDef);

                    // 摩西 PVE 被动【精密技巧】: 每层使闪避最小骰点 +1。层数从目标 buff 读取,
                    // 队友/观战对象的摩西同样适用(ShowForOthers 控制是否显示别人的战斗)。
                    // 有层数时显示骰面范围; 0 层时也显示一行, 避免看起来像功能没生效。
                    // 只在能看到 1251201 这个 PVE buff 时显示: PVP 摩西的被动不同, 不套用。
                    int mosesStacks = 0;
                    bool mosesHasPrecision = false;
                    string mosesLine = null;
                    if (_cfg.ShowMosesDodge && SafeInt(() => def.HeroId) == CombatMath.MosesHeroId)
                    {
                        var mosesBuffs = TargetBuffsOf(def.PlayerId);
                        mosesStacks = CombatMath.MosesDodgeStacks(CombatMath.MosesHeroId, mosesBuffs);
                        for (int i = 0; i < mosesBuffs.Count; i++)
                        {
                            if (mosesBuffs[i].BuffId != CombatMath.PrecisionBuffId) continue;
                            mosesHasPrecision = true;
                            break;
                        }
                        if (mosesStacks > 0)
                            mosesLine = "🔫 精密技巧×" + mosesStacks + " → 闪避骰 " + (mosesStacks + 1) + "~" + JudgeFaces();
                        else if (mosesHasPrecision)
                            mosesLine = "🔫 精密技巧×0（无闪避加成）";
                    }
                    int dodgeMinPoint = mosesStacks > 0 ? mosesStacks + 1 : 0;

                    // 防御 = MinDef..MaxDef 的范围随机值 + 一颗判定骰。
                    int flat; int[] dice;
                    CombatMath.RangeWithJudgeDice(SafeInt(() => def.MinDef), maxDef, out flat, out dice, JudgeFaces());

                    if (attackerThrew)
                    {
                        var d = CombatMath.Defend(finalAtk, hp, flat, dice, 1, dmgAdjust, targetImmune, dmgCap);
                        int dodgeFailDmg = _cfg.DodgeKeepsBaseDefense ? Math.Max(1, finalAtk - initDef) : Math.Max(1, finalAtk);
                        var dodge = CombatMath.Dodge(finalAtk, hp, atkPoint, JudgeFaces(), dodgeFailDmg, dmgAdjust, targetImmune, dmgCap, dodgeMinPoint);
                        bool dodgeSure = dodge.SuccessProb >= 1.0 - 1e-9;

                        bool defBetter = d.KnockdownProb < dodge.KnockdownProb - 1e-4;
                        bool dodgeBetter = d.KnockdownProb > dodge.KnockdownProb + 1e-4;

                        // 第 1 行: 一眼看懂的大字结论(带颜色)。
                        if (defBetter) sb.Append(Big(C("🛡 该防御", Blue)));
                        else if (dodgeBetter) sb.Append(Big(C(dodgeSure ? "💨 必闪" : "💨 该闪避", Green)));
                        else sb.Append(Big(C("⚖ 防闪都行", Gray)));

                        // 第 2 行: 两个被击倒率, 推荐项加 ★, 颜色按危险度。
                        sb.Append('\n').Append("🛡防 ").Append(KnockChip(d.KnockdownProb, defBetter))
                          .Append("    💨闪 ").Append(KnockChip(dodge.KnockdownProb, dodgeBetter));

                        // 第 3 行: 摩西的精技巧层数与闪避骰范围(单行, 只在有层数时出现)。
                        if (mosesLine != null) sb.Append('\n').Append(C(mosesLine, Yellow));

                        // 第 4 行: 情境小字。
                        sb.Append('\n').Append(Dim(defName + " " + hp + "血  <  " + atkName + " " + finalAtk + "攻"));
                    }
                    else
                    {
                        // 攻方未投：最大范围值 + 判定骰上限，作为最坏攻击。
                        int atkMax = SafeInt(() => atk.MaxAtk) + bonnieBonus + JudgeFaces();
                        var d = CombatMath.Defend(atkMax, hp, flat, dice, 1, dmgAdjust, targetImmune, dmgCap);
                        sb.Append(Big(C("⏳ 等对方投骰", Gray)));
                        sb.Append('\n').Append(Dim(defName + " " + hp + "血, 最坏被击倒 " + PctPlain(d.KnockdownProb)));
                        if (mosesLine != null) sb.Append('\n').Append(C(mosesLine, Yellow));
                    }
                    if (SafeBool(() => def.CanNotFightBack)) sb.Append('\n').Append(Dim("(本场无法反击)"));
                }

                // --------- 攻击方视角: 头行=击杀率(我方攻击 / 观战他人打怪都显示) ---------
                if (showDefender && showAttacker) sb.Append("\n─────────");
                if (showAttacker)
                {
                    int targetHp = HpOf(def.PlayerId);
                    int maxDef = SafeInt(() => def.MaxDef);

                    // 未投：攻击范围随机值 + 单颗判定骰；已投：最终攻击锁定，不能再加骰点。
                    int aFlat; int[] aDice;
                    if (attackerThrew) { aFlat = finalAtk; aDice = new int[0]; }
                    else
                    {
                        CombatMath.RangeWithJudgeDice(SafeInt(() => atk.MinAtk) + bonnieBonus,
                            SafeInt(() => atk.MaxAtk) + bonnieBonus, out aFlat, out aDice, JudgeFaces());
                    }

                    // 目标选择防御：防御范围随机值 + 单颗判定骰。
                    int dFlat; int[] dDice;
                    CombatMath.RangeWithJudgeDice(SafeInt(() => def.MinDef), maxDef, out dFlat, out dDice, JudgeFaces());

                    var r = CombatMath.Attack(aFlat, aDice, dFlat, dDice, targetHp, 1, dmgAdjust, targetImmune, dmgCap);
                    if (bonnieBonus > 0) sb.Append('\n').Append(C("邦妮标记目标攻击+3", Orange));
                    if (showDefender) sb.Append('\n');
                    sb.Append(Big("🎯 击杀 " + C(PctPlain(r.KillProb), KillColor(r.KillProb))));
                    sb.Append(Dim("（目标防御）"));
                    sb.Append('\n').Append(Dim(atkName + " > " + defName + " " + targetHp + "血 · 预计伤害 " + r.TypicalDamage));
                    if (SafeBool(() => def.CanNotFightBack)) sb.Append(Dim("  (无法反击)"));
                }

                // --------- buff: 一行短句说清"目标更容易/更难被打死"(数值已计入上面) ---------
                if (_cfg.ShowBuffBreakdown)
                {
                    if (targetImmune)
                        sb.Append('\n').Append(C("🛡 目标免疫这一击", Green));
                    else if (dmgAdjust > 0)
                        sb.Append('\n').Append(C("⚠ 目标易伤 受伤+" + dmgAdjust, Orange)).Append(Dim(BuffTail(badj.Applied)));
                    else if (dmgAdjust < 0)
                        sb.Append('\n').Append(C("🛡 目标减伤 受伤" + dmgAdjust, Green)).Append(Dim(BuffTail(badj.Applied)));
                    if (!targetImmune && dmgCap > 0)
                        sb.Append('\n').Append(C("🛡 目标每击最多受伤" + dmgCap, Green)).Append(Dim(BuffTail(badj.Applied)));
                }

                Publish(sb.ToString());
            }
            catch (Exception e)
            {
                SdkLog.Warn("CombatOdds", "战斗计算异常(已跳过本次): " + e.Message);
            }
        }

        // ============================== 显示 ==============================

        private static void Publish(string text)
        {
            // text 可能含 UBB 颜色标签(供 FightWindow 覆盖层); 控制台/通知用去标签的纯文本。
            string plain = StripUbb(text);
            _hud = plain;
            if (plain != _lastLogged)
            {
                _lastLogged = plain;
                // 控制台输出(带边框, 便于在滚动日志里一眼看到)。默认关闭: 这些读数已经显示在
                // 战斗窗口面板与通知里, 每次都写日志会把控制台刷满。
                if (_cfg.LogBattleOdds)
                    SdkLog.Info("CombatOdds", "\n┌─ 战斗胜率 ─────────────\n" + Indent(plain) + "\n└───────────────────────");
                // 通知只在真有 UI 渲染后端时弹。没有后端时 SDK 会把通知文本镜像进日志
                // ("无渲染后端时也保证可观察"), 而战斗读数已经显示在 FightWindow 覆盖层里,
                // 这个镜像只会把控制台刷满 —— 所以这里先判断 IsRenderingAvailable。
                if (_cfg.PopupNotification && UiService.IsRenderingAvailable)
                {
                    try { UiService.Notify(plain, UiNotificationLevel.Info, _cfg.NotificationTtl, _ctx); } catch { }
                }
            }
            // FightWindow 内嵌覆盖层(带颜色的富文本; 反射失败自动降级, 不影响上面的控制台/通知输出)。
            if (_cfg.InGameOverlay && _overlay != null)
            {
                try { _overlay.Update(true, text); } catch { }
            }
        }

        private static void Clear()
        {
            _hud = null;
            _lastLogged = null;
            if (_overlay != null) { try { _overlay.Hide(); } catch { } }
        }

        /// <summary>OnGUI 绘制(仅当安装了 UI 渲染后端时才会被 SDK 调用)。</summary>
        private static void DrawHud()
        {
            // 预留: 有 IMGUI/FairyGUI 后端时在此用 GUI.Label 画 _hud。
            // 本阶段无渲染后端, 此回调不会被调用; 保留以便后端就位后零改动生效。
        }

        // ============================== 辅助 ==============================

        private static int JudgeFaces()
        {
            try
            {
                int f = global::StaticGlobalData.GAME_JUDGE_DICE_LIMIT;
                return f > 0 ? f : CombatMath.DefaultJudgeDiceFaces;
            }
            catch { return CombatMath.DefaultJudgeDiceFaces; }
        }

        private static int BonnieMarkedMonsterBonus(BattleRole attacker, BattleRole defender)
        {
            try
            {
                var pd = Players.Get(attacker.PlayerId);
                if (pd?.player?.Hero == null) return 0;
                var target = Players.Get(defender.PlayerId);
                bool isMonster = target != null && target.characterType == CharacterType.Monster;
                if (!isMonster)
                {
                    foreach (var roster in Players.Roster())
                        if (roster.Id == defender.PlayerId && roster.IsMonster) { isMonster = true; break; }
                }
                if (!isMonster) return 0;
                bool marked = false;
                // 用原始进度: 标记归零后图标仍可能在, 不能算"带标记"。
                var buffs = TargetBuffsOf(defender.PlayerId);
                foreach (var buff in buffs)
                    if (buff.BuffId == 10006 && buff.Layers > 0) { marked = true; break; }
                return CombatMath.BonnieMarkedMonsterAttackBonus(pd.player.Hero.HeroId, isMonster, marked);
            }
            catch { return 0; }
        }

        private static bool IsMonster(long playerId)
        {
            try
            {
                var pd = Players.Get(playerId);
                if (pd != null) return pd.characterType == CharacterType.Monster;
                foreach (var stat in Players.Roster())
                    if (stat.Id == playerId) return stat.IsMonster;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 战斗角色的角色 id。优先用服务器下发的 <c>BattleRole.HeroId</c>；
        /// 取不到时回退到战斗玩家表里的 Hero 快照, 供【渊蚀印记】这类按来源判定的 buff 使用。
        /// </summary>
        private static int HeroIdOf(BattleRole role)
        {
            if (role == null) return 0;
            int id = SafeInt(() => role.HeroId);
            if (id > 0) return id;
            try
            {
                var hero = Players.Get(role.PlayerId)?.player?.Hero;
                if (hero != null) return hero.HeroId;
            }
            catch { }
            return 0;
        }

    }
}
