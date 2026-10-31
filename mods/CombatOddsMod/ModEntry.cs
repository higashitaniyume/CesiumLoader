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
    /// <summary>
    /// 战斗胜率助手 —— 在打怪投牌(FightWindow)界面实时计算并显示:
    ///   · 防御: 被击倒率
    ///   · 闪避: 被击倒率 (d6 判定, 精确)
    ///   · 我方攻击: 预计伤害(伤害分布的众数, 无唯一众数时取中位数并四舍五入) + 击杀概率
    /// 每次攻/防数值或骰点变化(BattleUpdate 去重)即重算一次, 满足"每投一张更新一次"。
    /// 攻方投出 0 点时数值不变, 由 GameEvents.BattlePhaseChanged 补一次重算。
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
    /// 计算模型与限制:
    ///   · 攻击 = MinAtk..MaxAtk 的随机整数 + 一颗判定骰，防御同理使用 MinDef..MaxDef。
    ///     范围内整数暂按等概率且与判定骰独立；不能从上限推断为 N 颗 d6。
    ///   · 最终攻击锁定后直接使用 Atk；伤害基线 max(1, Atk-Def)，普通命中保底 1。
    ///     特殊 buff 在保底之后修正伤害；无特殊效果时，1 血目标选择防御必被击倒。
    ///   · 击杀率以目标选择防御为条件，不混合闪避，也不是整场获胜率。
    ///   · 闪避按单颗判定骰计算，保证闪避等特殊效果不在通用模型内。
    ///   · 已登记的受伤 buff 通过 Players.BuffsOf 读取并计入；未登记效果无法保证已覆盖。
    ///   · 旧回放拟合结果不证明本模型的范围等概率假设，当前需继续真机核对。
    /// </summary>
    public static partial class ModEntry
    {
        private static CombatOddsConfig _cfg = new CombatOddsConfig();
        private static ModContext _ctx;
        private static string _hud;              // 当前 HUD 文本(供 OnGUI/覆盖层渲染)
        private static string _lastLogged;       // 控制台去重
        private static FightOverlayController _overlay;  // FightWindow 内嵌覆盖层(真机验证)
        private static Battle _lastBattle;
        private static readonly Dictionary<int, CombatMath.BuffDamageEffect> _buffTable = CombatMath.DefaultBuffTable();

        /// <summary>
        /// HUD 上"我们算的最终攻击"用蓝色 —— 与游戏自己那个白色数字区分开, 一眼能认出哪个是模组算的。
        /// (游戏原来的数字我们一个字节都不改, 所以最终就是"两个数字"并列。)
        /// </summary>
        private static readonly UnityEngine.Color FinalAtkColor = new UnityEngine.Color32(0x5A, 0xA9, 0xFF, 0xFF);

        // 主 HUD(左下角)攻击力后面的"筹码加成"追加显示。
        private static HudAtkBonusOverlay _hudBonus;
        // 自己角色卡上的移速加成(查看筹码按钮正上方)。
        private static MoveBonusOverlay _moveBonus;
        private static string _lastMoveBonusLog;     // 移速日志去重
        private static SkillCooldownOverlay _skillCooldown;
        private static MonsterCooldownOverlay _monsterCooldown;
        private static HandCountOverlay _handCount;
        private static MegasBombardmentOverlay _megasDamage;
        private static PlayerAttrOverlay _playerAttrOverlay;
        private static object _atkPropBound;        // 已挂监听的 ATK 属性(用于换绑/解绑)
        private static Action<int> _atkListener;    // 保活引用, 便于 RemoveListener
        private static string _lastAtkBonusLog;     // 诊断日志去重(只在加成文本变化时打一行)
        private static readonly Dictionary<string, float> _recentDamageEvents = new Dictionary<string, float>();
        private static float _lastDamagePruneTime;
        private static readonly Dictionary<long, string> _lastAshDiagnostic = new Dictionary<long, string>();

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
        private static void LoadConfig(bool isInitial = false)
        {
            try
            {
                var c = _ctx != null ? _ctx.Config : null;
                var d = new CombatOddsConfig();   // 默认值来源
                if (c != null)
                {
                    bool docsChanged = CombatOddsConfigDocs.Apply(c);
                    _cfg.Enabled               = c.GetBool("Enabled", d.Enabled);
                    _cfg.ShowForOthers         = c.GetBool("ShowForOthers", d.ShowForOthers);
                    _cfg.PopupNotification     = c.GetBool("PopupNotification", d.PopupNotification);
                    _cfg.NotificationTtl       = c.GetFloat("NotificationTtl", d.NotificationTtl);
                    _cfg.DodgeKeepsBaseDefense = c.GetBool("DodgeKeepsBaseDefense", d.DodgeKeepsBaseDefense);
                    _cfg.ColorHighlight        = c.GetBool("ColorHighlight", d.ColorHighlight);
                    _cfg.ShowBuffBreakdown     = c.GetBool("ShowBuffBreakdown", d.ShowBuffBreakdown);
                    _cfg.ShowMosesDodge        = c.GetBool("ShowMosesDodge", d.ShowMosesDodge);
                    _cfg.InGameOverlay         = c.GetBool("InGameOverlay", d.InGameOverlay);
                    _cfg.OverlayFontScale      = c.GetDouble("OverlayFontScale", d.OverlayFontScale);
                    _cfg.OverlayAnchor         = c.GetString("OverlayAnchor", d.OverlayAnchor);
                    _cfg.OverlayOffsetX        = c.GetDouble("OverlayOffsetX", d.OverlayOffsetX);
                    _cfg.OverlayOffsetY        = c.GetDouble("OverlayOffsetY", d.OverlayOffsetY);
                    _cfg.ShowRelicAtkBonus     = c.GetBool("ShowRelicAtkBonus", d.ShowRelicAtkBonus);
                    _cfg.ShowBoardPlayerAttrs  = c.GetBool("ShowBoardPlayerAttrs", d.ShowBoardPlayerAttrs);
                    _cfg.LogAtkBonusDetail     = c.GetBool("LogAtkBonusDetail", d.LogAtkBonusDetail);
                    _cfg.LogBattleOdds         = c.GetBool("LogBattleOdds", d.LogBattleOdds);
                    _cfg.ShowSkillCooldown     = c.GetBool("ShowSkillCooldown", d.ShowSkillCooldown);
                    _cfg.ShowHandCount         = c.GetBool("ShowHandCount", d.ShowHandCount);
                    _cfg.LogHandDetail         = c.GetBool("LogHandDetail", d.LogHandDetail);
                    _cfg.ShowMegasDamage       = c.GetBool("ShowMegasDamage", d.ShowMegasDamage);
                    _cfg.ShowMonsterSkillCooldown = c.GetBool("ShowMonsterSkillCooldown", d.ShowMonsterSkillCooldown);
                    _cfg.ShowMonsterCdForAllMonsters = c.GetBool("ShowMonsterCdForAllMonsters", d.ShowMonsterCdForAllMonsters);
                    _cfg.MonsterCdOffsetX      = c.GetDouble("MonsterCdOffsetX", d.MonsterCdOffsetX);
                    _cfg.MonsterCdOffsetY      = c.GetDouble("MonsterCdOffsetY", d.MonsterCdOffsetY);
                    _cfg.ShowMoveBonus         = c.GetBool("ShowMoveBonus", d.ShowMoveBonus);
                    _cfg.MoveBonusFontSize     = c.GetDouble("MoveBonusFontSize", d.MoveBonusFontSize);
                    _cfg.MoveBonusOffsetX      = c.GetDouble("MoveBonusOffsetX", d.MoveBonusOffsetX);
                    _cfg.MoveBonusOffsetY      = c.GetDouble("MoveBonusOffsetY", d.MoveBonusOffsetY);
                    _cfg.LogMoveBonusDetail    = c.GetBool("LogMoveBonusDetail", d.LogMoveBonusDetail);
                    // Debug 构建(deploy-combatodds.ps1 部署的那份)默认打开 CD 明细日志, 免去改配置。
#if COMBATODDS_DEBUG_PROBES
                    _cfg.LogCdDetail       = c.GetBool("LogCdDetail", true);
#else
                    _cfg.LogCdDetail       = c.GetBool("LogCdDetail", d.LogCdDetail);
#endif

                    // 仅初始化时补齐缺失键, 避免热重载写坏外部文件。
                    // 已有配置也补: 新版本新增的显示项(如 ShowHandCount)要在 Toys 配置页里能看到。
                    if (isInitial && (!c.Has("Enabled") || !c.Has("ShowHandCount") || !c.Has("LogHandDetail") ||
                        !c.Has("ShowMegasDamage") || !c.Has("ShowMoveBonus") ||
                        !c.Has("ShowMonsterSkillCooldown") || !c.Has("ShowMonsterCdForAllMonsters")))
                    {
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
                        c.Set("LogBattleOdds", _cfg.LogBattleOdds);
                        c.Set("ShowSkillCooldown", _cfg.ShowSkillCooldown);
                        c.Set("ShowHandCount", _cfg.ShowHandCount);
                        c.Set("LogHandDetail", _cfg.LogHandDetail);
                        c.Set("ShowMegasDamage", _cfg.ShowMegasDamage);
                        c.Set("ShowMonsterSkillCooldown", _cfg.ShowMonsterSkillCooldown);
                        c.Set("ShowMonsterCdForAllMonsters", _cfg.ShowMonsterCdForAllMonsters);
                        c.Set("MonsterCdOffsetX", _cfg.MonsterCdOffsetX);
                        c.Set("MonsterCdOffsetY", _cfg.MonsterCdOffsetY);
                        c.Set("ShowMoveBonus", _cfg.ShowMoveBonus);
                        c.Set("MoveBonusFontSize", _cfg.MoveBonusFontSize);
                        c.Set("MoveBonusOffsetX", _cfg.MoveBonusOffsetX);
                        c.Set("MoveBonusOffsetY", _cfg.MoveBonusOffsetY);
                        c.Set("LogMoveBonusDetail", _cfg.LogMoveBonusDetail);
                        c.Set("LogCdDetail", _cfg.LogCdDetail);
                        if (!c.SaveIfDirty()) SdkLog.Warn("CombatOdds", "配置未能保存，请检查 mod 目录写入权限。");
                    }
                    else if (docsChanged)
                    {
                        if (!c.SaveIfDirty()) SdkLog.Warn("CombatOdds", "配置说明未能保存，请检查 mod 目录写入权限。");
                    }
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

        private static void OnConfigChanged()
        {
            LoadConfig(isInitial: false);
            if (_skillCooldown != null)
            {
                _skillCooldown.SetEnabled(_cfg.ShowSkillCooldown);
                _skillCooldown.SetLogDetail(_cfg.LogCdDetail);
            }
            if (_monsterCooldown != null)
            {
                _monsterCooldown.SetEnabled(_cfg.ShowMonsterSkillCooldown);
                _monsterCooldown.SetShowAll(_cfg.ShowMonsterCdForAllMonsters);
                _monsterCooldown.SetOffset(_cfg.MonsterCdOffsetX, _cfg.MonsterCdOffsetY);
                _monsterCooldown.SetLogDetail(_cfg.LogCdDetail);
            }
            if (_playerAttrOverlay != null)
            {
                _playerAttrOverlay.SetEnabled(_cfg.ShowBoardPlayerAttrs);
                _playerAttrOverlay.SetShowMove(_cfg.ShowMoveBonus);
            }
            if (_handCount != null)
            {
                _handCount.SetEnabled(_cfg.ShowHandCount);
                _handCount.SetLogDetail(_cfg.LogHandDetail);
            }
            if (_megasDamage != null)
            {
                _megasDamage.SetEnabled(_cfg.ShowMegasDamage);
            }
            if (_hudBonus != null)
            {
                RefreshHudAtkBonus();
            }
            if (_moveBonus != null)
            {
                _moveBonus.SetLayout(_cfg.MoveBonusFontSize, _cfg.MoveBonusOffsetX, _cfg.MoveBonusOffsetY);
                RefreshHudMoveBonus();
            }
            SdkLog.Info("CombatOdds", "配置已热重载");
        }

        private static void OnInit()
        {
            _ctx = ModRegistry.GetOrRegister(typeof(ModEntry).Assembly);
            if (_ctx?.Config != null) _ctx.Config.Changed += _ => OnConfigChanged();
            LoadConfig(isInitial: true);
            if (!_cfg.Enabled)
            {
                SdkLog.Warn("CombatOdds", "配置 Enabled=false, mod 已停用");
                return;
            }
            GameEvents.BattleUpdate += OnBattleUpdate;
            GameEvents.BattlePhaseChanged += OnBattlePhaseChanged;
            GameEvents.CardUsed += OnCombatCardUsed;
            GameEvents.EffectCardUsed += OnCombatEffectCardUsed;
            GameEvents.QuickCardUsed += OnCombatQuickCardUsed;
            GameEvents.HeroAttrUpdated += OnHeroAttrUpdated;
            GameEvents.SkillUsed += OnSkillUsed;
            GameEvents.StartAutoHook();

            // FightWindow 内嵌覆盖层(FairyGUI 反射, 需进游戏目视确认位置; 失败自动降级到控制台)。
            try { _overlay = new FightOverlayController(new RuntimeFightOverlayReflector()); }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "覆盖层初始化失败(仅用控制台): " + e.Message); }

            try
            {
                _skillCooldown = new SkillCooldownOverlay();
                _skillCooldown.SetEnabled(_cfg.ShowSkillCooldown);
                _skillCooldown.SetLogDetail(_cfg.LogCdDetail);
                _skillCooldown.Start();
                _ctx?.RegisterCleanup(_skillCooldown.Dispose);
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "技能 CD 覆盖层初始化失败: " + e.Message); }

            // 怪物名字/信息条旁的主动技能 CD(释放时服务器校准, 之后按轮推算)。
            try
            {
                _monsterCooldown = new MonsterCooldownOverlay();
                _monsterCooldown.SetEnabled(_cfg.ShowMonsterSkillCooldown);
                _monsterCooldown.SetShowAll(_cfg.ShowMonsterCdForAllMonsters);
                _monsterCooldown.SetOffset(_cfg.MonsterCdOffsetX, _cfg.MonsterCdOffsetY);
                _monsterCooldown.SetLogDetail(_cfg.LogCdDetail);
                _monsterCooldown.Start();
                _ctx?.RegisterCleanup(_monsterCooldown.Dispose);
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "怪物 CD 覆盖层初始化失败: " + e.Message); }

            // 左侧 HUD 每名玩家头像上的剩余手牌数(出牌/摸牌/换牌后随实时数据刷新)。
            try
            {
                _handCount = new HandCountOverlay(_skillCooldown);
                _handCount.SetEnabled(_cfg.ShowHandCount);
                _handCount.SetLogDetail(_cfg.LogHandDetail);
                _handCount.Start();
                _ctx?.RegisterCleanup(_handCount.Dispose);
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "手牌计数覆盖层初始化失败: " + e.Message); }

            // 主 HUD 攻击力加成(星币锤/手电筒/美工刀): 在左下角攻击力右侧显示, 可悬浮看明细。
            try
            {
                _hudBonus = new HudAtkBonusOverlay();
                _hudBonus.StartHoverPolling();   // 轮询兜底: 不依赖 FairyGUI 的命中测试
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "HUD 攻击力加成初始化失败(仅不显示加成): " + e.Message); }

            // 自己角色卡上的移速加成(查看筹码按钮正上方): 加速/减速来自服务器下发的 AddMove。
            try
            {
                _moveBonus = new MoveBonusOverlay();
                _moveBonus.SetLayout(_cfg.MoveBonusFontSize, _cfg.MoveBonusOffsetX, _cfg.MoveBonusOffsetY);
                _ctx?.RegisterCleanup(_moveBonus.Reset);
                // 投骰差值(服务器实际加的移速) = 唯一包含筹码常驻加成的来源; 游戏每场会重置 RPC, 由 tick 重挂。
                ThrowDiceProbe.BasePredictor = PredictedBaseOf;
                ThrowDiceProbe.EnsureHooked();
                _ctx?.RegisterCleanup(ThrowDiceProbe.Unhook);
                // 学习"哪些 Buff 会加/减移速", 用于投骰前预测。
                MoveBuffTracker.EnsureHooked();
                _ctx?.RegisterCleanup(MoveBuffTracker.Unhook);
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "移速加成覆盖层初始化失败(仅不显示移速): " + e.Message); }

            // 玩梅加斯时, 右下角发动技能按钮上方显示轨道轰炸理论伤害(回放同样显示)。
            try
            {
                _megasDamage = new MegasBombardmentOverlay();
                _megasDamage.SetEnabled(_cfg.ShowMegasDamage);
                _megasDamage.Start();
                _ctx?.RegisterCleanup(_megasDamage.Dispose);
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "梅加斯理论伤害覆盖层初始化失败: " + e.Message); }

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
            // 移速加成: 加速/减速随 Buff、筹码与地图事件变化, 每秒读一次即可。
            // 两个 RPC 记录器也要每秒重挂一次(游戏每场战斗会重置 RPC 回调)。
            ThrowDiceProbe.EnsureHooked();
            MoveBuffTracker.EnsureHooked();
            RefreshHudMoveBonus();
            // 棋盘头顶攻防: 位置/数值的实时刷新已由 PlayerAttrOverlay 自己的每帧(LateUpdate)回调负责,
            // 这里每秒一次只用于把配置开关同步过去(不承担跟随刷新, 否则又会变成低刷新率)。
            if (_playerAttrOverlay != null)
            {
                _playerAttrOverlay.SetEnabled(_cfg.ShowBoardPlayerAttrs);
                _playerAttrOverlay.SetShowMove(_cfg.ShowMoveBonus);
            }
        }

        // ============================== 核心: 战斗更新 ==============================

        private static int _lastOrbitalDiscardCount;
        private static int _lastOrbitalDiscardCost;

        private static void OnCombatCardUsed(long playerId, int cardId, int remain)
        {
            RefreshAfterCombatCard("BattleCard#" + cardId);
        }

        private static void OnCombatEffectCardUsed(long playerId, int cardId, int remain)
        {
            RefreshAfterCombatCard("EffectCard#" + cardId);
        }

        private static void OnCombatQuickCardUsed(long playerId, int cardId, int originalCardId)
        {
            RefreshAfterCombatCard("QuickCard#" + cardId);
        }

        private static void RefreshAfterCombatCard(string source)
        {
            try
            {
                // 有人出牌: 向服务器查询一次所有人的技能冷却(覆盖层下一次刷新时发出/更新)。
                _skillCooldown?.RequestQueryAll();
                if (_lastBattle == null) return;
                SdkLog.Info("CombatOdds", "出牌后重新计算战斗概率；来源=" + source);
                OnBattleUpdate(_lastBattle);
            }
            catch { }
        }

        private static void OnSkillUsed(long playerId, int skillId)
        {
            try
            {
                if (skillId == 12203)
                {
                    var hand = Players.HandSnapshot(playerId);
                    _lastOrbitalDiscardCount = hand.Item1;
                    _lastOrbitalDiscardCost = hand.Item2;
                    int count = CombatMath.MegasOrbitalBombardmentCount(hand.Item1);
                    int damage = CombatMath.MegasOrbitalBombardmentDamage(hand.Item1, hand.Item2);
                    int total = CombatMath.MegasOrbitalBombardmentTotalDamage(hand.Item1, hand.Item2);
                    SdkLog.Info("CombatOdds", "[技能估算] 梅加斯·轨道轰炸：弃牌 " + hand.Item1 + " 张，总费用 " + hand.Item2 +
                        "；轰炸次数 " + count + "；每次伤害 " + damage + "；理论总伤害 " + total +
                        "；随机目标为6格内怪物(由服务器决定)");
                }
                else if (skillId == 12902 || skillId == 11403 || skillId == 12702)
                {
                    string label = CombatMath.KnownSkillLabel(skillId);
                    SdkLog.Info("CombatOdds", "[技能识别] " + label + "；释放者=" + playerId);
                }
            }
            catch { }
        }

        private static void OnHeroAttrUpdated(party.protocol.UpdateHeroAttrS2C update)
        {
            try
            {
                if (update?.Cause == null || update.EffectDatas == null) return;
#if COMBATODDS_DEBUG_PROBES
                LogPhoenixProbe(update);
#endif
                foreach (var effect in update.EffectDatas)
                {
                    var hp = effect?.Hp;
                    if (hp == null || hp.RealChangeHp >= 0) continue;
                    string source = update.Cause.S + "#" + update.Cause.Id;
                    string eventKey = hp.PlayerId + "|" + source + "|" + hp.RealChangeHp + "|" + hp.OriHp + "|" + hp.CurrHp;
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (_recentDamageEvents.TryGetValue(eventKey, out var seenAt) && now - seenAt < 0.5f) continue;
                    _recentDamageEvents[eventKey] = now;
                    if (now - _lastDamagePruneTime > 10f)
                    {
                        _lastDamagePruneTime = now;
                        var expired = new List<string>();
                        foreach (var item in _recentDamageEvents)
                            if (now - item.Value > 2f) expired.Add(item.Key);
                        foreach (var key in expired) _recentDamageEvents.Remove(key);
                    }
                    var context = CombatMath.ContextFromDamageType(hp.DamageType);
                    // Cause.Id 只在技能/召唤物语境里能对到配表 id(卡牌/事件/地图事件是另一套 id 空间)。
                    // 召唤物伤害(DamageType=召唤)也要带上, 否则魔渊触须那一下没有来源可判。
                    bool causeIsConfigId = update.Cause.S == party.protocol.CauseOrigin.Types.source.Skill
                        || context == CombatMath.DamageContext.Summon;
                    long contextCauseId = causeIsConfigId ? update.Cause.Id : 0;
                    string seaxCause = CombatMath.IsSeaxDamageCause(contextCauseId) ? "（赛克斯/魔渊触须）" : string.Empty;
                    bool targetIsMonster = IsMonster(hp.PlayerId);
                    var buffs = BuffAdjOf(hp.PlayerId, context, contextCauseId, targetIsMonster);
#if COMBATODDS_DEBUG_PROBES
                    LogAshDiagnostic(hp.PlayerId, buffs);
#endif
                    string skillLabel = update.Cause.S == party.protocol.CauseOrigin.Types.source.Skill
                        ? CombatMath.KnownSkillLabel(update.Cause.Id) : null;
                    string line = "[实际伤害] 目标 " + hp.PlayerId + " 受到 " + (-hp.RealChangeHp) +
                        " 点伤害；DamageType=" + context + "；Cause=" + source + seaxCause +
                        (skillLabel != null ? "（" + skillLabel + "）" : string.Empty) +
                        (buffs.Applied.Count > 0 ? "；已匹配修正=" + BuffTail(buffs.Applied) : string.Empty) +
                        (buffs.ContextOnly.Count > 0 ? "；其他语境提示=" + string.Join("、", buffs.ContextOnly) : string.Empty) +
                        (hp.Killer != 0 ? "；Killer=" + hp.Killer : string.Empty);
                    SdkLog.Info("CombatOdds", line);
                }
            }
            catch { }
        }


    }
}
