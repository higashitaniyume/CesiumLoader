using System;
using System.Collections.Generic;
using System.Text;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;
using GameLogic;
using Tools;

namespace CombatOddsMod
{
    public static partial class ModEntry
    {
        // ============================== 主 HUD: 攻击力 + 筹码加成 ==============================

        /// <summary>
        /// 把「星币锤 / 手电筒 / 美工刀」带来的攻击力加成, 显示在左下角主 HUD 攻击力数字的<b>旁边</b>。
        /// 每个筹码一个 "+N"(星币锤/手电筒黄色, 美工刀绿色, 两个美工刀就是两个 +N);
        /// 鼠标移到某个 "+N" 上会弹出说明浮框(见 <see cref="HudAtkBonusOverlay"/>)。
        ///
        /// 实现要点: <b>不修改游戏自己的攻击力元素</b> —— 加成画在另外新建的文本元素上,
        /// 所以游戏怎么刷新它自己的数字都不影响我们, 反之亦然。
        /// 只作用于自己(BattleInfoPanel 显示的本来就是自己)。
        /// </summary>
        private static void RefreshHudAtkBonus()
        {
            if (_hudBonus == null) return;
            try
            {
                if (_cfg == null || !_cfg.ShowRelicAtkBonus)
                {
                    // 开关关掉: 只是把我们自己那个元素藏起来, 游戏 HUD 原样不动。
                    _hudBonus.Apply(null, UnityEngine.Color.white, null);
                    return;
                }

                var pd = SelfPlayerData();
                if (pd == null || pd.Property == null)
                {
                    // 不在战斗/大盘: 面板本来就不在, 清缓存等下次重新定位。
                    _hudBonus.Reset();
                    UnbindAtk();
                    return;
                }

                EnsureAtkBinding(pd);

                int atk = SafeInt(() => pd.Property.ATK.Value);
                RelicAtkBonus.Input input;
                var bonuses = CurrentAtkBonuses(pd, out input);

                if (bonuses.Count == 0)
                {
                    // 没有任何加成时不必再显示一个和游戏一模一样的数字
                    _hudBonus.Apply(null, UnityEngine.Color.white, null);
                }
                else
                {
                    int total = atk + RelicAtkBonus.Total(bonuses);
                    _hudBonus.Apply(total.ToString(), FinalAtkColor, BuildTip(atk, total, bonuses, input));
                }
                LogAtkBonusIfChanged(atk, input, bonuses, pd);
            }
            catch
            {
                // 每秒调用一次, 失败保持静默, 绝不刷日志。
            }
        }

        private static BattlePlayerData SelfPlayerData()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                return gm != null && gm.battle != null ? gm.battle.GetSelfPlayerData() : null;
            }
            catch { return null; }
        }

        /// <summary>读自己当前的筹码与各项实时数值, 交给纯逻辑算出各筹码的攻击力加成。</summary>
        internal static List<RelicAtkBonus.Bonus> CurrentAtkBonuses(BattlePlayerData pd, out RelicAtkBonus.Input input)
        {
            input = new RelicAtkBonus.Input();
            try
            {
                var ids = pd.GetRelicIds();
                if (ids != null)
                {
                    for (int i = 0; i < ids.Count; i++)
                    {
                        switch (ids[i])
                        {
                            case RelicAtkBonus.CoinHammerId: input.HasCoinHammer = true; break;
                            case RelicAtkBonus.FlashlightId: input.HasFlashlight = true; break;
                            case RelicAtkBonus.KnifeBasicId: input.HasKnifeBasic = true; break;
                            case RelicAtkBonus.KnifeSharpId: input.HasKnifeSharp = true; break;
                        }
                    }
                }

                // 层数取【实时计数器】而不是 Hero.CureNum/SalaryNum —— 后者只是构造 BattleProperty 时的初值,
                // 真正随 S2C(HeroCureNumChange / HeroSalaryNumChange)更新的是 Property.CureCount/SalaryCount
                // 这两个 PropertyData(buff 10004 治愈 / 10005 星光)。取不到才退回 Hero 上的快照值。
                bool cureOk = false, salaryOk = false;
                try { input.CureLayers = pd.Property.CureCount.property.Value; cureOk = true; } catch { }
                try { input.SalaryLayers = pd.Property.SalaryCount.property.Value; salaryOk = true; } catch { }

                var hero = pd.player != null ? pd.player.Hero : null;
                if (hero != null)
                {
                    if (!cureOk) input.CureLayers = hero.CureNum;
                    if (!salaryOk) input.SalaryLayers = hero.SalaryNum;
                }

                input.Gold = SafeInt(() => pd.Property.gold.Value);
                input.Hp = SafeInt(() => pd.Property.HP.Value);
                input.MaxHp = SafeInt(() => pd.Property.maxHP);
            }
            catch { }
            return RelicAtkBonus.Evaluate(input);
        }

        /// <summary>把加成拼成 "+32 +7 +7" 这样的纯文本(只用于日志; HUD 上是每个筹码一个独立元素)。</summary>
        private static string BonusText(List<RelicAtkBonus.Bonus> bonuses)
        {
            if (bonuses == null || bonuses.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int k = 0; k < bonuses.Count; k++)
            {
                sb.Append('+').Append(bonuses[k].Value);
                if (k < bonuses.Count - 1) sb.Append(' ');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 悬浮说明: 讲清"最终攻击这个数是怎么加出来的" —— 基础 + 每个筹码的 +N(各自带颜色),
        /// 再附一行灰色的算式依据。用 UBB 富文本(游戏自己的筹码说明也是这么渲染的)。
        /// </summary>
        private static string BuildTip(int baseAtk, int total, List<RelicAtkBonus.Bonus> bonuses, RelicAtkBonus.Input input)
        {
            var sb = new StringBuilder();
            // 总值也用蓝色, 跟 HUD 上那个蓝色数字对上
            sb.Append("最终攻击 [color=#").Append(Blue).Append(']').Append(total).Append("[/color]").Append('\n');
            sb.Append(baseAtk).Append("（基础）");
            for (int i = 0; i < bonuses.Count; i++)
            {
                var b = bonuses[i];
                sb.Append("  [color=#").Append(Hex(b.Color)).Append(']')
                  .Append('+').Append(b.Value).Append("（").Append(b.Name).Append("）")
                  .Append("[/color]");
            }

            // 算式依据(灰字)
            var why = new StringBuilder();
            for (int i = 0; i < bonuses.Count; i++)
            {
                if (why.Length > 0) why.Append("；");
                var b = bonuses[i];
                switch (b.RelicId)
                {
                    case RelicAtkBonus.CoinHammerId:
                        why.Append("星币锤＝星币").Append(input.Gold).Append("的30%四舍五入");
                        break;
                    case RelicAtkBonus.FlashlightId:
                        why.Append("手电筒＝星光").Append(input.SalaryLayers).Append("层÷4");
                        break;
                    case RelicAtkBonus.KnifeBasicId:
                        why.Append("美工刀初级＝满血按治愈").Append(input.CureLayers).Append("层");
                        break;
                    case RelicAtkBonus.KnifeSharpId:
                        why.Append("美工刀锋利＝满血按治愈").Append(input.CureLayers).Append("层");
                        break;
                }
            }
            if (why.Length > 0)
                sb.Append("\n[color=#BFBFBF]").Append(why).Append("[/color]");
            return sb.ToString();
        }

        /// <summary>加成的 UBB 颜色(与 HUD 上那个数字同一套)。</summary>
        private static string Hex(RelicAtkBonus.Tint tint)
        {
            return tint == RelicAtkBonus.Tint.Green ? Green : Yellow;
        }

        /// <summary>
        /// 挂到自己的 ATK 响应式属性上: 攻击力数字变宽变窄时, 我们那几个 "+N" 要跟着挪位置,
        /// 否则会压在数字上(我们不改游戏的元素, 只是紧跟着它重新排版)。
        /// </summary>
        private static void EnsureAtkBinding(BattlePlayerData pd)
        {
            try
            {
                var prop = pd.Property != null ? pd.Property.ATK : null;
                if (prop == null || ReferenceEquals(prop, _atkPropBound)) return;
                UnbindAtk();
                if (_atkListener == null) _atkListener = OnSelfAtkChanged;
                prop.AddListener(_atkListener, false);   // false = 不立即回调(tick 马上会排一次)
                _atkPropBound = prop;
            }
            catch { }
        }

        private static void UnbindAtk()
        {
            try
            {
                if (_atkPropBound != null && _atkListener != null)
                    RuntimeAssemblyService.SafeInvoke(_atkPropBound, "RemoveListener", new object[] { _atkListener }, "CombatOdds.hud");
            }
            catch { }
            _atkPropBound = null;
        }

        private static void OnSelfAtkChanged(int atk)
        {
            // 攻击力数字变了 → 立刻重新排版我们的 "+N"(失败也无所谓, 下一秒 tick 会兜底)。
            RefreshHudAtkBonus();
        }

        /// <summary>
        /// 诊断日志: 只在"加成文本有变化"时打一行, 把这一刻的原始输入(星币/治愈/星光/血量/持有筹码)
        /// 和算出来的加成一起记下来 —— 用来跟游戏里的结算伤害对拍, 确认各筹码的公式/门槛。
        /// 默认开启(调试期); 稳定后可把配置 LogAtkBonusDetail 关掉。
        /// </summary>
        private static void LogAtkBonusIfChanged(int atk, RelicAtkBonus.Input input, List<RelicAtkBonus.Bonus> bonuses,
            BattlePlayerData pd)
        {
            if (_cfg == null || !_cfg.LogAtkBonusDetail) return;
            try
            {
                string plain = BonusText(bonuses);
                string sign = atk + "|" + plain + "|" + input.Gold + "|" + input.CureLayers + "|" + input.SalaryLayers
                              + "|" + input.Hp + "/" + input.MaxHp;
                if (sign == _lastAtkBonusLog) return;
                _lastAtkBonusLog = sign;

                int total = atk + RelicAtkBonus.Total(bonuses);
                SdkLog.Info("CombatOdds",
                    "[hud] 基础攻击 " + atk + (total > atk ? "" : "(无加成)")
                    + " → HUD 显示最终攻击 " + total
                    + "  | 星币=" + input.Gold + " 治愈=" + input.CureLayers + " 星光=" + input.SalaryLayers
                    + " 血=" + input.Hp + "/" + input.MaxHp
                    + " | 持有筹码: " + RelicSummary(pd)
                    + " | 加成明细: " + BonusDetail(bonuses));
            }
            catch { }
        }

        /// <summary>把持有的筹码列成 "50043 星币锤, 50010 美工刀-初级" 这样, 便于核对识别是否正确。</summary>
        private static string RelicSummary(BattlePlayerData pd)
        {
            try
            {
                var ids = pd.GetRelicIds();
                if (ids == null || ids.Count == 0) return "(无)";
                var sb = new StringBuilder();
                for (int i = 0; i < ids.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ids[i]);
                    string nm = RelicName(ids[i]);
                    if (nm != null) { sb.Append(' ').Append(nm); }
                }
                return sb.ToString();
            }
            catch { return "?"; }
        }

        private static string RelicName(int id)
        {
            switch (id)
            {
                case RelicAtkBonus.CoinHammerId: return "星币锤";
                case RelicAtkBonus.FlashlightId: return "手电筒-强光";
                case RelicAtkBonus.KnifeBasicId: return "美工刀-初级";
                case RelicAtkBonus.KnifeSharpId: return "美工刀-锋利";
                default: return null;
            }
        }

        private static string BonusDetail(List<RelicAtkBonus.Bonus> bonuses)
        {
            if (bonuses == null || bonuses.Count == 0) return "(无)";
            var sb = new StringBuilder();
            for (int i = 0; i < bonuses.Count; i++)
            {
                if (i > 0) sb.Append(" + ");
                sb.Append(bonuses[i].Name).Append('=').Append(bonuses[i].Value);
            }
            return sb.ToString();
        }
    }
}
