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
                    // Debug 构建(deploy-combatodds.ps1 部署的那份)默认打开 CD 明细日志, 免去改配置。
#if COMBATODDS_DEBUG_PROBES
                    _cfg.LogCdDetail       = c.GetBool("LogCdDetail", true);
#else
                    _cfg.LogCdDetail       = c.GetBool("LogCdDetail", d.LogCdDetail);
#endif

                    // 仅初始化时补齐缺失键, 避免热重载写坏外部文件。
                    // 已有配置也补: 新版本新增的显示项(如 ShowHandCount)要在 Toys 配置页里能看到。
                    if (isInitial && (!c.Has("Enabled") || !c.Has("ShowHandCount") || !c.Has("LogHandDetail") ||
                        !c.Has("ShowMegasDamage") ||
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
            // 棋盘头顶攻防: 位置/数值的实时刷新已由 PlayerAttrOverlay 自己的每帧(LateUpdate)回调负责,
            // 这里每秒一次只用于把配置开关同步过去(不承担跟随刷新, 否则又会变成低刷新率)。
            if (_playerAttrOverlay != null) _playerAttrOverlay.SetEnabled(_cfg.ShowBoardPlayerAttrs);
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
