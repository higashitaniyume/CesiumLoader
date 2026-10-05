using System;
using System.Collections.Generic;
using System.Text;
using CesiumLoader.SDK;
using GameLogic;
using party.model;

namespace CombatOddsMod
{
    /// <summary>
    /// 战斗胜率助手 —— 在打怪投牌(FightWindow)界面实时计算并显示:
    ///   · 防御: 期望受到伤害 + 被击倒率
    ///   · 闪避: 被击倒率 (d6 判定, 精确)
    ///   · 我方攻击: 期望伤害 + 击杀概率
    /// 每次攻/防数值或骰点变化(BattleUpdate 去重)即重算一次, 满足"每投一张更新一次"。
    ///
    /// 数据来源(反编译验证): 服务器把 party.model.BattleRole 的
    ///   Atk/Def、MinAtk/MaxAtk、MinDef/MaxDef、InitAtk/InitDef、Point、Dodge、CanNotFightBack
    /// 全部下发到客户端(UI.FightWindow 就是拿这些画出"防3+1~6+1~10"的)。血量从
    /// BattlePlayerData.Property.HP.Value 读。战斗结果本身由服务器权威判定, 本 mod 只读+算, 不发指令。
    ///
    /// 显示(本阶段):
    ///   · 加载器控制台(SdkLog) —— 100% 可用, 模组作者本来就看控制台。
    ///   · UiService 覆盖层 + OnGUI 回调登记 —— 已就位; 游戏内可视化覆盖层需渲染后端,
    ///     属于下一阶段(FairyGUI 注入 / IMGUI 泵), 需进游戏迭代验证。
    ///
    /// 精度说明(已用 11 局真实回放对拍校准 CombatMath):
    ///   · 攻击/防御构成: Atk = 基础+骰点、Def = InitDef+防御骰点 —— 回放逐帧精确吻合。
    ///   · 伤害基线 = max(1, Atk-Def): 命中保底 1(回放里 Atk&lt;=Def 时防守方仍几乎必掉 1 血);
    ///     干净样本精确率由 max(0,·) 的 54% 提升到 68%。余下 +1~+3 为效果类附加伤害。
    ///   · 闪避被击倒率: 精确(单颗 d6 对阈值), 规则 defPoint&gt;atkPoint 在回放中 37/43 一致,
    ///     少数例外为保证闪避的特殊效果。
    ///   · 防御/攻击随机项 = 基础 + N×d6(回放实测: 投骰 Val 恒 1..6, Def-InitDef 92% 落 1..6 单 d6,
    ///     少数 7..12 为叠加第二颗 d6)。故用 d6 模型的【精确】骰池, 已弃用旧的区间均匀近似(不再标 "≈")。
    ///     骰子颗数按可达上限推断, 默认单 d6。
    ///   · 目标身上"受到伤害±N"类 buff(标记/狂暴/护盾免疫等)通过 Players.BuffsOf 实时读取并计入;
    ///     未在 buff 表内的效果不影响伤害, 不予处理。
    /// </summary>
    public static class ModEntry
    {
        private static CombatOddsConfig _cfg = new CombatOddsConfig();
        private static ModContext _ctx;
        private static string _hud;              // 当前 HUD 文本(供 OnGUI/覆盖层渲染)
        private static string _lastLogged;       // 控制台去重
        private static FightOverlayController _overlay;  // FightWindow 内嵌覆盖层(真机验证)
        private static readonly Dictionary<int, CombatMath.BuffDamageEffect> _buffTable = CombatMath.DefaultBuffTable();

        /// <summary>
        /// HUD 上"我们算的最终攻击"用蓝色 —— 与游戏自己那个白色数字区分开, 一眼能认出哪个是模组算的。
        /// (游戏原来的数字我们一个字节都不改, 所以最终就是"两个数字"并列。)
        /// </summary>
        private static readonly UnityEngine.Color FinalAtkColor = new UnityEngine.Color32(0x5A, 0xA9, 0xFF, 0xFF);

        // 主 HUD(左下角)攻击力后面的"筹码加成"追加显示。
        private static HudAtkBonusOverlay _hudBonus;
        private static PlayerAttrOverlay _playerAttrOverlay;
        private static object _atkPropBound;        // 已挂监听的 ATK 属性(用于换绑/解绑)
        private static Action<int> _atkListener;    // 保活引用, 便于 RemoveListener
        private static string _lastAtkBonusLog;     // 诊断日志去重(只在加成文本变化时打一行)

        public static void Main()
        {
            try { MainSafe(); }
            catch (Exception ex)
            {
                SdkLog.ReportCrash("CombatOdds", "ModEntry.Main 顶层异常", ex);
                throw;
            }
        }

        private static void MainSafe()
        {
            SdkLog.Info("CombatOdds", "=== 战斗胜率助手启动 ===");
            SdkManifest.ExportSidecar();
            ModBase.Run(init: OnInit, tick: OnTick, tag: "CombatOdds");
        }

        /// <summary>
        /// 从 <c>mods\CombatOddsMod\config.json</c>(DLL 同目录)读配置, 并把默认值补齐后落盘。
        /// 这个位置正是 AstralParty.Toys「模组」页 ⚙ 配置表单读写的文件, 因此字号/位置/开关
        /// 都能在 Toys 里用勾选框/数字框/文本框直接改(改完重启游戏生效)。
        /// </summary>
        private static void LoadConfig()
        {
            try
            {
                var c = _ctx != null ? _ctx.Config : null;
                var d = new CombatOddsConfig();   // 默认值来源
                if (c != null)
                {
                    _cfg.Enabled               = c.GetBool("Enabled", d.Enabled);
                    _cfg.ShowForOthers         = c.GetBool("ShowForOthers", d.ShowForOthers);
                    _cfg.PopupNotification     = c.GetBool("PopupNotification", d.PopupNotification);
                    _cfg.NotificationTtl       = c.GetFloat("NotificationTtl", d.NotificationTtl);
                    _cfg.DodgeKeepsBaseDefense = c.GetBool("DodgeKeepsBaseDefense", d.DodgeKeepsBaseDefense);
                    _cfg.ColorHighlight        = c.GetBool("ColorHighlight", d.ColorHighlight);
                    _cfg.ShowBuffBreakdown     = c.GetBool("ShowBuffBreakdown", d.ShowBuffBreakdown);
                    _cfg.InGameOverlay         = c.GetBool("InGameOverlay", d.InGameOverlay);
                    _cfg.OverlayFontScale      = c.GetDouble("OverlayFontScale", d.OverlayFontScale);
                    _cfg.OverlayAnchor         = c.GetString("OverlayAnchor", d.OverlayAnchor);
                    _cfg.OverlayOffsetX        = c.GetDouble("OverlayOffsetX", d.OverlayOffsetX);
                    _cfg.OverlayOffsetY        = c.GetDouble("OverlayOffsetY", d.OverlayOffsetY);
                    _cfg.ShowRelicAtkBonus     = c.GetBool("ShowRelicAtkBonus", d.ShowRelicAtkBonus);
                    _cfg.ShowBoardPlayerAttrs  = c.GetBool("ShowBoardPlayerAttrs", d.ShowBoardPlayerAttrs);
                    _cfg.LogAtkBonusDetail     = c.GetBool("LogAtkBonusDetail", d.LogAtkBonusDetail);

                    // 全字段写回, 保证 config.json 始终含全部键 → Toys ⚙ 表单能把每一项都列出来。
                    c.Set("Enabled", _cfg.Enabled);
                    c.Set("ShowForOthers", _cfg.ShowForOthers);
                    c.Set("PopupNotification", _cfg.PopupNotification);
                    c.Set("NotificationTtl", _cfg.NotificationTtl);
                    c.Set("DodgeKeepsBaseDefense", _cfg.DodgeKeepsBaseDefense);
                    c.Set("ColorHighlight", _cfg.ColorHighlight);
                    c.Set("ShowBuffBreakdown", _cfg.ShowBuffBreakdown);
                    c.Set("InGameOverlay", _cfg.InGameOverlay);
                    c.Set("OverlayFontScale", _cfg.OverlayFontScale);
                    c.Set("OverlayAnchor", string.IsNullOrEmpty(_cfg.OverlayAnchor) ? d.OverlayAnchor : _cfg.OverlayAnchor);
                    c.Set("OverlayOffsetX", _cfg.OverlayOffsetX);
                    c.Set("OverlayOffsetY", _cfg.OverlayOffsetY);
                    c.Set("ShowRelicAtkBonus", _cfg.ShowRelicAtkBonus);
                    c.Set("ShowBoardPlayerAttrs", _cfg.ShowBoardPlayerAttrs);
                    c.Set("LogAtkBonusDetail", _cfg.LogAtkBonusDetail);
                    try { c.Save(); } catch { }
                }
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "读配置失败, 用默认值: " + e.Message); }

            // 覆盖层布局参数(字号/锚点/偏移) → 反射器静态字段。
            double sc = _cfg.OverlayFontScale > 0 ? _cfg.OverlayFontScale : 1.0;
            RuntimeFightOverlayReflector.BaseFontSize = System.Math.Max(12, (int)System.Math.Round(28 * sc));
            RuntimeFightOverlayReflector.Anchor = string.IsNullOrEmpty(_cfg.OverlayAnchor) ? "left-center" : _cfg.OverlayAnchor;
            RuntimeFightOverlayReflector.OffsetX = (float)_cfg.OverlayOffsetX;
            RuntimeFightOverlayReflector.OffsetY = (float)_cfg.OverlayOffsetY;
        }

        private static void OnInit()
        {
            _ctx = ModContext.Current;
            LoadConfig();
            if (!_cfg.Enabled)
            {
                SdkLog.Warn("CombatOdds", "配置 Enabled=false, mod 已停用");
                return;
            }
            GameEvents.BattleUpdate += OnBattleUpdate;
            GameEvents.StartAutoHook();

            // FightWindow 内嵌覆盖层(FairyGUI 反射, 需进游戏目视确认位置; 失败自动降级到控制台)。
            try { _overlay = new FightOverlayController(new RuntimeFightOverlayReflector()); }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "覆盖层初始化失败(仅用控制台): " + e.Message); }

            // 主 HUD 攻击力加成(星币锤/手电筒/美工刀): 在左下角攻击力右侧显示, 可悬浮看明细。
            try
            {
                _hudBonus = new HudAtkBonusOverlay();
                _hudBonus.StartHoverPolling();   // 轮询兜底: 不依赖 FairyGUI 的命中测试
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "HUD 攻击力加成初始化失败(仅不显示加成): " + e.Message); }

            try
            {
                _playerAttrOverlay = new PlayerAttrOverlay();
                _playerAttrOverlay.Start();
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "棋盘玩家攻防覆盖层初始化失败: " + e.Message); }

            // 登记覆盖层 + OnGUI 回调(有渲染后端时会被画出; 无后端时仅登记, 不影响控制台输出)。
            try
            {
                UiService.RegisterOverlay("combat-odds-hud", visible: true, owner: _ctx);
                UiService.RegisterGuiCallback(DrawHud, _ctx, "combat-odds");
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "UI 登记失败(不影响控制台输出): " + e.Message); }

            SdkLog.Info("CombatOdds", "已订阅战斗事件, 等待打怪/PK。判定骰面数=" + JudgeFaces());
        }

        private static void OnTick()
        {
            // 事件挂钩由 SDK 内部维持; 本 mod 无需轮询逻辑。
            GameEvents.EnsureHooked();
            // HUD 攻击力加成: 星币/血量/层数变化不触发 ATK 事件, 靠这每秒一次兜底刷新。
            RefreshHudAtkBonus();
            if (_playerAttrOverlay != null) _playerAttrOverlay.Update(_cfg.ShowBoardPlayerAttrs);
        }

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
                var gm = Tools.SimpleSingletonProvider<GameLogicManager>.inst;
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

        // ============================== 核心: 战斗更新 ==============================

        private static void OnBattleUpdate(Battle b)
        {
            try
            {
                if (b == null || b.Attacker == null || b.Defender == null) return;
                if (b.IsEnd) { Clear(); return; }

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

                int atkPoint = SafeInt(() => atk.Point);
                int finalAtk = SafeInt(() => atk.Atk);
                bool attackerThrew = atkPoint > 0;

                // 目标(防守方/被击中者)身上的实时 buff → 受伤修正(易伤/减伤/免疫)。
                // 攻、守两个视角命中的都是同一个"被击中者"= 防守方, 故只求一次。
                var badj = BuffAdjOf(def.PlayerId);
                int dmgAdjust = badj.Delta;
                bool targetImmune = badj.Immune;

                // --------- 防守方视角: 一眼看该防还是该闪 ---------
                if (showDefender)
                {
                    int hp = HpOf(def.PlayerId);
                    int initDef = SafeInt(() => def.InitDef);
                    int maxDef = SafeInt(() => def.MaxDef);

                    // 防御骰: InitDef + N×d6(默认单 d6, 精确骰池)。
                    int flat; int[] dice;
                    CombatMath.D6ModelFromRange(initDef, System.Math.Max(maxDef, initDef + 6), out flat, out dice);

                    if (attackerThrew)
                    {
                        var d = CombatMath.Defend(finalAtk, hp, flat, dice, 1, dmgAdjust, targetImmune);
                        int dodgeFailDmg = _cfg.DodgeKeepsBaseDefense ? Math.Max(0, finalAtk - initDef) : finalAtk;
                        var dodge = CombatMath.Dodge(finalAtk, hp, atkPoint, JudgeFaces(), dodgeFailDmg, dmgAdjust, targetImmune);

                        bool defBetter = d.KnockdownProb < dodge.KnockdownProb - 1e-4;
                        bool dodgeBetter = d.KnockdownProb > dodge.KnockdownProb + 1e-4;

                        // 第 1 行: 一眼看懂的大字结论(带颜色)。
                        if (defBetter) sb.Append(Big(C("🛡 该防御", Blue)));
                        else if (dodgeBetter) sb.Append(Big(C("💨 该闪避", Green)));
                        else sb.Append(Big(C("⚖ 防闪都行", Gray)));

                        // 第 2 行: 两个被击倒率, 推荐项加 ★, 颜色按危险度。
                        sb.Append('\n').Append("🛡防 ").Append(KnockChip(d.KnockdownProb, defBetter))
                          .Append("    💨闪 ").Append(KnockChip(dodge.KnockdownProb, dodgeBetter));

                        // 第 3 行: 情境小字。
                        sb.Append('\n').Append(Dim(defName + " " + hp + "血  <  " + atkName + " " + finalAtk + "攻"));
                    }
                    else
                    {
                        // 攻方未投: 阈值未知无法算闪; 用最大攻做保守防御估计。
                        int atkMax = SafeInt(() => atk.MaxAtk);
                        var d = CombatMath.Defend(atkMax, hp, flat, dice, 1, dmgAdjust, targetImmune);
                        sb.Append(Big(C("⏳ 等对方投骰", Gray)));
                        sb.Append('\n').Append(Dim(defName + " " + hp + "血, 最坏被击倒 " + PctPlain(d.KnockdownProb)));
                    }
                    if (SafeBool(() => def.CanNotFightBack)) sb.Append('\n').Append(Dim("(本场无法反击)"));
                }

                // --------- 攻击方视角: 头行=击杀率(我方攻击 / 观战他人打怪都显示) ---------
                if (showDefender && showAttacker) sb.Append("\n─────────");
                if (showAttacker)
                {
                    int targetHp = HpOf(def.PlayerId);
                    int maxDef = SafeInt(() => def.MaxDef);
                    int tgtInitDef = SafeInt(() => def.InitDef);

                    // 我方攻击: 未投则 = 当前基础 + N×d6(默认单 d6); 已投则锁定 finalAtk。
                    int aFlat; int[] aDice;
                    if (attackerThrew) { aFlat = finalAtk; aDice = new int[0]; }
                    else CombatMath.D6ModelFromRange(SafeInt(() => atk.Atk), SafeInt(() => atk.MaxAtk), out aFlat, out aDice);

                    // 目标防御: InitDef + N×d6(d6 模型, 精确骰池)。
                    int dFlat; int[] dDice;
                    CombatMath.D6ModelFromRange(tgtInitDef, System.Math.Max(maxDef, tgtInitDef + 6), out dFlat, out dDice);

                    var r = CombatMath.Attack(aFlat, aDice, dFlat, dDice, targetHp, 1, dmgAdjust, targetImmune);
                    if (showDefender) sb.Append('\n');
                    sb.Append(Big("🎯 击杀 " + C(PctPlain(r.KillProb), KillColor(r.KillProb))));
                    sb.Append('\n').Append(Dim(atkName + " > " + defName + " " + targetHp + "血 · 期望伤害 " + Fmt1(r.ExpectedDamage)));
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
                // 控制台输出(带边框, 便于在滚动日志里一眼看到)。
                SdkLog.Info("CombatOdds", "\n┌─ 战斗胜率 ─────────────\n" + Indent(plain) + "\n└───────────────────────");
                // 通知(有渲染后端时更显眼; 无后端时也进日志)。
                if (_cfg.PopupNotification)
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

        /// <summary>读目标身上的实时 buff, 换算成普通投牌战斗的受伤修正(易伤/减伤/免疫)。</summary>
        private static CombatMath.BuffAdjustment BuffAdjOf(long targetId)
        {
            try
            {
                var raw = Players.BuffsOf(targetId);
                if (raw != null && raw.Count > 0)
                {
                    var tb = new List<CombatMath.TargetBuff>(raw.Count);
                    foreach (var b in raw) tb.Add(new CombatMath.TargetBuff(b.BuffId, b.Layers));
                    return CombatMath.EvaluateTargetBuffs(tb, _buffTable);
                }
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

        private static string Pct(double p) => (p * 100.0).ToString("0.#") + "%";
        // 右对齐到 5 位("100%"/" 62%"/"  8%"), 让防御/闪避两行的被击倒率竖排对齐, 一眼比大小。
        private static string PadPct(double p)
        {
            string s = System.Math.Round(p * 100.0).ToString("0") + "%";
            return s.Length >= 4 ? s : new string(' ', 4 - s.Length) + s;
        }
        private static string Fmt1(double v) => v.ToString("0.#");

        // ============================== 颜色/排版(UBB) ==============================
        // FairyGUI GTextField 开启 ubbEnabled 后支持 [color=#rgb]/[size=n]/[b]。
        // ColorHighlight=false 或送控制台时, 一律降级为纯文本(StripUbb)。
        private const string Blue = "5AA9FF";
        private const string Green = "5BE37A";
        private const string Gray = "BFBFBF";
        private const string Orange = "FFB454";
        private const string Red = "FF6B6B";
        private const string Yellow = "FFD24A";

        private static bool Color => _cfg != null && _cfg.ColorHighlight;

        /// <summary>按配置缩放字号(默认已是放大版)。</summary>
        private static int Sz(int baseSize)
        {
            double s = _cfg != null && _cfg.OverlayFontScale > 0 ? _cfg.OverlayFontScale : 1.0;
            int v = (int)System.Math.Round(baseSize * s);
            return v < 10 ? 10 : v;
        }

        private static string C(string text, string hex) => Color ? ("[color=#" + hex + "]" + text + "[/color]") : text;
        private static string Big(string text) => Color ? ("[size=" + Sz(40) + "]" + text + "[/size]") : text;
        private static string Dim(string text) => Color ? ("[size=" + Sz(22) + "][color=#" + Gray + "]" + text + "[/color][/size]") : text;

        /// <summary>被击倒率彩片: 颜色按危险度(绿低/黄中/红高), 推荐项(更安全)前加 ★。</summary>
        private static string KnockChip(double p, bool recommended)
        {
            string hex = p < 0.20 ? Green : (p < 0.50 ? Yellow : Red);
            string s = (recommended ? "★" : "") + PctPlain(p);
            return C(s, hex);
        }

        /// <summary>击杀率颜色: 越高越绿(对攻方是好事)。</summary>
        private static string KillColor(double p) => p >= 0.60 ? Green : (p >= 0.30 ? Yellow : Red);

        private static string PctPlain(double p) => System.Math.Round(p * 100.0).ToString("0") + "%";

        /// <summary>buff 尾注: " (标记x2 / 狂暴)" —— 最多两项, 简短。</summary>
        private static string BuffTail(System.Collections.Generic.List<string> applied)
        {
            if (applied == null || applied.Count == 0) return string.Empty;
            var names = new System.Collections.Generic.List<string>();
            foreach (var a in applied)
            {
                // a 形如 "标记x2 +2" / "狂暴 +1"; 只取名字部分。
                int sp = a.IndexOf(' ');
                names.Add(sp > 0 ? a.Substring(0, sp) : a);
                if (names.Count >= 2) break;
            }
            return "  (" + string.Join(" / ", names.ToArray()) + ")";
        }

        private static readonly System.Text.RegularExpressions.Regex _ubb =
            new System.Text.RegularExpressions.Regex(@"\[/?(color|size|b|i|u)(=[^\]]*)?\]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>去掉 UBB 标签(送控制台/通知时用, 免得日志里全是 [color] 标记)。</summary>
        private static string StripUbb(string s) => string.IsNullOrEmpty(s) ? s : _ubb.Replace(s, string.Empty);

        private static string Indent(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return "│ " + s.Replace("\n", "\n│ ");
        }
    }

    /// <summary>战斗胜率助手配置(configs/CombatOddsMod.json, 全部可选)。</summary>
    public class CombatOddsConfig
    {
        /// <summary>总开关。</summary>
        public bool Enabled = true;
        /// <summary>是否也为非自己的战斗显示(观战/队友打怪与防御)。默认开启, 满足"别人打怪防御也要有显示"。</summary>
        public bool ShowForOthers = true;
        /// <summary>是否弹出游戏内通知(有渲染后端时可见; 无后端时进日志)。</summary>
        public bool PopupNotification = true;
        /// <summary>通知存活秒数。</summary>
        public float NotificationTtl = 8f;
        /// <summary>闪避失败时是否仍保留基础防御(InitDef)减伤(默认否, 按承受全额攻击算, 更保守)。</summary>
        public bool DodgeKeepsBaseDefense = false;
        /// <summary>是否用颜色高亮结论与关键数字(FightWindow 覆盖层富文本; 控制台自动降级为纯文本)。</summary>
        public bool ColorHighlight = true;
        /// <summary>是否显示目标身上"受到伤害±"类 buff 明细(标记/狂暴/护盾/蓝海晴印记等), 并把它们计入伤害与击倒率。</summary>
        public bool ShowBuffBreakdown = true;
        /// <summary>是否把读数以 FairyGUI 覆盖层显示在 FightWindow 上(真机验证; 失败自动降级到控制台/通知)。</summary>
        public bool InGameOverlay = true;
        /// <summary>覆盖层字号缩放(1.0=默认已放大版; 想更大调 1.2~1.5, 想更小调 0.8)。</summary>
        public double OverlayFontScale = 1.0;
        /// <summary>覆盖层锚点: "left-center"(默认, 靠左竖直居中) / "top-left" / "top-center" / "top-right" / "right-center"。</summary>
        public string OverlayAnchor = "left-center";
        /// <summary>覆盖层水平微调(像素, 正=右移)。</summary>
        public double OverlayOffsetX = 0;
        /// <summary>覆盖层垂直微调(像素, 正=下移; 靠左居中时可用负值上移)。</summary>
        public double OverlayOffsetY = 0;
        /// <summary>是否在主 HUD(左下角)的攻击力后面追加显示星币锤/手电筒/美工刀带来的攻击力加成。</summary>
        public bool ShowRelicAtkBonus = true;
        /// <summary>是否在棋盘上每个玩家头顶显示实时攻击/防御, 悬停查看详情。</summary>
        public bool ShowBoardPlayerAttrs = true;
        /// <summary>把每次攻击的攻击力加成明细打进加载器日志(星币/治愈/星光/血量/持有筹码 + 各筹码加成), 便于与结算伤害对拍。</summary>
        public bool LogAtkBonusDetail = true;
    }
}
