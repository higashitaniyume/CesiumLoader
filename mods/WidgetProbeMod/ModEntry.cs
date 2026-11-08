using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.UserInterface;
using UnityEngine;

namespace WidgetProbeMod
{
    /// <summary>入口: <c>WidgetProbeMod.ModEntry.Main()</c>。</summary>
    public static class ModEntry
    {
        public static void Main() { ModBase.Run(new WidgetProbe()); }
    }

    /// <summary>
    /// WidgetService + 新版 GameEvents 的实机探针。
    ///
    /// 用 WidgetService 在屏幕右上角画一个常驻面板, 用新版 GameEvents
    /// (商店/筹码富事件、商店成交、筹码结果、血量差值、击杀、刷新次数、downSn)
    /// 喂数据: 事件一到, 面板上立刻出现原始字段与换算后的名字, 证明
    /// "WidgetService 能画" 且 "新事件真能收到"。
    ///
    /// 热键: F9 显隐面板, F8 清空流水与计数 (config.json 可改)。
    /// </summary>
    public sealed class WidgetProbe : ModBase
    {
        // ---------- 配置 ----------
        private bool _enabled = true;
        private KeyCode _toggleKey = KeyCode.F9;
        private KeyCode _clearKey = KeyCode.F8;
        private int _fontSize = 14;

        // ---------- 面板句柄 ----------
        private WidgetScope _scope;
        private WidgetHandle _canvas;
        private WidgetHandle _panel;
        private WidgetHandle _title;
        private WidgetHandle _body;
        private WidgetHandle _badge;
        private WidgetHandle _badgeText;
        private WidgetHandle _hpSlider;
        private WidgetHandle _hpLabel;
        private bool _panelVisible = true;
        private int _panelFontSize;
        private long _retryAtTicks;

        // ---------- 事件状态 (事件线程写, OnUpdate 读) ----------
        private readonly object _gate = new object();
        private readonly List<string> _feed = new List<string>();
        private readonly Dictionary<long, int> _hpDeltaSum = new Dictionary<long, int>();
        private readonly Dictionary<long, int> _rerollLeft = new Dictionary<long, int>();
        private readonly Dictionary<long, int> _killCount = new Dictionary<long, int>();

        private ShopOffer _lastShopOffer;
        private RelicOffer _lastRelicOffer;
        private ShopSettlement _lastSettlement;
        private long _lastRelicPlayer;
        private int _lastRelicId;
        private bool _lastRelicIsReroll;
        private long _lastRelicDownSn;
        private long _lastHpPlayer;
        private int _lastHpDelta;
        private int _lastHpMax;
        private long _lastVictim;
        private long _lastAttacker;
        private long _selfId;
        private bool _selfKnown;

        private int _cShopOffer;
        private int _cRelicOffer;
        private int _cSettlement;
        private int _cRelicResult;
        private int _cRelicSelected;
        private int _cRerollChanged;
        private int _cHpChanged;
        private int _cKilled;
        private int _cShopCand;
        private int _cRelicCand;

        private long _nextRefreshTicks;

        public override string Name { get { return "Widget 探针"; } }
        public override string Version { get { return "0.1.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get { return "右上角 HUD 面板: WidgetService 绘制 + 新版 GameEvents 喂数据 (商店/筹码富事件、成交、血量、击杀、刷新、downSn)。"; }
        }

        public override void OnInitialize()
        {
            ReadConfig(true);

            // 只订阅“新版观测”事件 + 旧事件各两个用来交叉核对。
            GameEvents.ShopOffered += OnShopOffered;
            GameEvents.RelicOffered += OnRelicOffered;
            GameEvents.ShopSettled += OnShopSettled;
            GameEvents.RelicResult += OnRelicResult;
            GameEvents.RelicSelected += OnRelicSelected;
            GameEvents.RelicRerollChanged += OnRelicRerollChanged;
            GameEvents.HeroHpChanged += OnHeroHpChanged;
            GameEvents.PlayerKilled += OnPlayerKilled;
            GameEvents.ShopCandidates += OnShopCandidates;
            GameEvents.RelicCandidates += OnRelicCandidates;
            GameEvents.StartAutoHook();

            Context?.RegisterCleanup(TearDown);
            PushFeed("探针启动, 订阅 10 个观测事件 (StartAutoHook 已开)");

            if (BuildPanel())
            {
                Log.Info("观测面板已创建: WidgetService.IsAvailable=true, 快捷键 " +
                         _toggleKey + "=显隐 " + _clearKey + "=清空");
            }
            else
            {
                Log.Warn("观测面板首次创建失败 (" + WidgetService.UnavailableReason +
                         "), 将在 5 秒后在 OnUpdate 重试");
                _retryAtTicks = UtcNowTicks() + 5L * TimeSpan.TicksPerSecond;
            }
        }

        public override void OnConfigChanged()
        {
            ReadConfig(false);
            Log.Info("配置已热重载: enabled=" + _enabled + ", toggleKey=" + _toggleKey +
                     ", clearKey=" + _clearKey + ", fontSize=" + _fontSize);
        }

        public override void OnUpdate()
        {
            try
            {
                if (!UiService.ShouldBlockGameInput)
                {
                    if (InputService.IsKeyPressed(_toggleKey)) TogglePanel();
                    else if (InputService.IsKeyPressed(_clearKey)) ClearState();
                }
            }
            catch { }

            // uGUI 还没就绪时延迟重试 (OnInitialize 时类型可能尚未加载)。
            if (ReferenceEquals(_canvas, null) || !IsAlive(_canvas))
            {
                long now = UtcNowTicks();
                if (_retryAtTicks == 0 || now >= _retryAtTicks) BuildPanel();
                return;
            }

            // 配置改了字号就重建面板。
            if (_panelFontSize != _fontSize) { BuildPanel(); return; }

            long tick = UtcNowTicks();
            if (tick < _nextRefreshTicks) return;
            _nextRefreshTicks = tick + TimeSpan.TicksPerMillisecond * 250;

            RefreshPanel();
        }

        public override void OnUnload()
        {
            GameEvents.ShopOffered -= OnShopOffered;
            GameEvents.RelicOffered -= OnRelicOffered;
            GameEvents.ShopSettled -= OnShopSettled;
            GameEvents.RelicResult -= OnRelicResult;
            GameEvents.RelicSelected -= OnRelicSelected;
            GameEvents.RelicRerollChanged -= OnRelicRerollChanged;
            GameEvents.HeroHpChanged -= OnHeroHpChanged;
            GameEvents.PlayerKilled -= OnPlayerKilled;
            GameEvents.ShopCandidates -= OnShopCandidates;
            GameEvents.RelicCandidates -= OnRelicCandidates;
            TearDown();
        }

        // =====================================================================
        // 配置 / 热键
        // =====================================================================

        private void ReadConfig(bool initial)
        {
            var config = Config;
            if (config == null) return;
            if (initial)
            {
                WidgetProbeConfigDocs.Apply(config);
                config.SetDefault("enabled", true);
                config.SetDefault("toggleKey", "F9");
                config.SetDefault("clearKey", "F8");
                config.SetDefault("fontSize", 14);
            }
            _enabled = config.GetBool("enabled", true);
            _toggleKey = ParseKey(config.GetString("toggleKey", "F9"), KeyCode.F9);
            _clearKey = ParseKey(config.GetString("clearKey", "F8"), KeyCode.F8);
            _fontSize = Math.Max(12, Math.Min(20, config.GetInt("fontSize", 14)));
            if (initial) config.SaveIfDirty();
        }

        private static KeyCode ParseKey(string name, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            try
            {
                KeyCode parsed;
                return Enum.TryParse(name, true, out parsed) ? parsed : fallback;
            }
            catch { return fallback; }
        }

        private void TogglePanel()
        {
            _panelVisible = !_panelVisible;
            if (_panel != null) _panel.SetActive(_panelVisible && _enabled);
            Log.Info("面板已" + (_panelVisible ? "显示" : "隐藏"));
        }

        private void ClearState()
        {
            lock (_gate)
            {
                _lastShopOffer = null;
                _lastRelicOffer = null;
                _lastSettlement = null;
                _lastHpPlayer = 0;
                _lastVictim = 0;
                _feed.Clear();
                _hpDeltaSum.Clear();
                _rerollLeft.Clear();
                _killCount.Clear();
                _cShopOffer = _cRelicOffer = _cSettlement = _cRelicResult = _cRelicSelected = 0;
                _cRerollChanged = _cHpChanged = _cKilled = _cShopCand = _cRelicCand = 0;
                _feed.Add("已清空, 开始新一轮观测");
            }
            RefreshPanel();
            Log.Info("观测状态已清空");
        }

        // =====================================================================
        // 面板构建 (Widgets)
        // =====================================================================

        private bool BuildPanel()
        {
            TearDown();

            if (!_enabled) { _canvas = null; return false; }
            if (!WidgetService.IsAvailable)
            {
                _retryAtTicks = UtcNowTicks() + 5L * TimeSpan.TicksPerSecond;
                return false;
            }

            WidgetService.EnsureEventSystem();
            _scope = WidgetService.CreateScope("widget-probe-hud");

            _canvas = _scope.Track(WidgetService.Canvas("WidgetProbeCanvas", sortingOrder: 1200, persist: true));
            if (_canvas == null || !IsAlive(_canvas))
            {
                _retryAtTicks = UtcNowTicks() + 5L * TimeSpan.TicksPerSecond;
                TearDown();
                return false;
            }

            // 面板贴左上角 (ShopRelicPeek 的面板在右上角, 避免重叠)。
            // 注意: RectTransform 默认 pivot 是中心点, Place 的 position 是 pivot 相对锚点的位置,
            // 所以必须先把 pivot 对齐到锚角再 Place, 否则半块面板会伸出屏幕外。
            _panel = WidgetService.Panel(_canvas.Rect, new Color(0.05f, 0.07f, 0.10f, 0.80f), rounded: true);
            Place(_panel.Rect, WidgetAnchor.TopLeft, new Vector2(16, -16), new Vector2(400, 560));

            _title = WidgetService.Text(_panel.Rect, "SDK 观测面板", 18, Color.white, WidgetAnchor.TopLeft);
            Place(_title.Rect, WidgetAnchor.TopLeft, new Vector2(16, -10), new Vector2(260, 28));

            _badge = WidgetService.Badge(_panel.Rect, new Color(0.35f, 0.38f, 0.45f, 0.95f), rounded: true);
            Place(_badge.Rect, WidgetAnchor.TopRight, new Vector2(-16, -10), new Vector2(96, 26));

            _badgeText = WidgetService.Text(_badge.Rect, "等待事件", 13, Color.white, WidgetAnchor.Center);
            WidgetService.Stretch(_badgeText.Rect);

            _body = WidgetService.Text(_panel.Rect, "…", _fontSize, new Color(0.92f, 0.95f, 1f, 1f), WidgetAnchor.TopLeft);
            Place(_body.Rect, WidgetAnchor.TopLeft, new Vector2(16, -48), new Vector2(368, 428));

            _hpSlider = WidgetService.Slider(_panel.Rect, 0f, 100f, 0f);
            Place(_hpSlider.Rect, WidgetAnchor.Bottom, new Vector2(0, 12), new Vector2(360, 18));

            _hpLabel = WidgetService.Text(_panel.Rect, "自己血量: —", 12, Color.white, WidgetAnchor.Center);
            Place(_hpLabel.Rect, WidgetAnchor.Bottom, new Vector2(0, 36), new Vector2(240, 16));

            _panel.SetActive(_panelVisible);
            _panelFontSize = _fontSize;

            // 建完立刻把各 setter 跑一遍, 写进日志作为自检证据。
            bool ok = _title.SetText("SDK 观测面板")
                      && _badgeText.SetText("等待事件")
                      && _hpLabel.SetText("自己血量: —")
                      && _panel.SetActive(_panelVisible);
            Log.Info("面板构建自检 " + (ok ? "PASS" : "FAIL") +
                     ": Canvas/Panel/Title/Badge/Body/Slider/Place/SetText/SetActive/SetValue 均已调用");
            RefreshPanel();
            return true;
        }

        private void TearDown()
        {
            try { if (_scope != null) _scope.Dispose(); } catch { }
            _scope = null;
            _canvas = null;
            _panel = null;
            _title = null;
            _body = null;
            _badge = null;
            _badgeText = null;
            _hpSlider = null;
            _hpLabel = null;
        }

        private static bool IsAlive(WidgetHandle handle)
        {
            return handle != null && handle.IsAlive;
        }

        // Place 之前先把 pivot 对齐到锚角 (WidgetService.Place 只设锚点/偏移/尺寸)。
        private static bool Place(RectTransform rt, WidgetAnchor corner, Vector2 position, Vector2 size)
        {
            if (ReferenceEquals(rt, null)) return false;
            try
            {
                switch (corner)
                {
                    case WidgetAnchor.TopLeft: rt.pivot = new Vector2(0f, 1f); break;
                    case WidgetAnchor.Top: rt.pivot = new Vector2(0.5f, 1f); break;
                    case WidgetAnchor.TopRight: rt.pivot = new Vector2(1f, 1f); break;
                    case WidgetAnchor.MiddleLeft: rt.pivot = new Vector2(0f, 0.5f); break;
                    case WidgetAnchor.MiddleRight: rt.pivot = new Vector2(1f, 0.5f); break;
                    case WidgetAnchor.BottomLeft: rt.pivot = new Vector2(0f, 0f); break;
                    case WidgetAnchor.Bottom: rt.pivot = new Vector2(0.5f, 0f); break;
                    case WidgetAnchor.BottomRight: rt.pivot = new Vector2(1f, 0f); break;
                    default: rt.pivot = new Vector2(0.5f, 0.5f); break;
                }
            }
            catch { }
            return WidgetService.Place(rt, corner, position, size);
        }

        // =====================================================================
        // 面板刷新 (只在主线程 OnUpdate 调用)
        // =====================================================================

        private void RefreshPanel()
        {
            if (!IsAlive(_body)) return;

            // 先拉一次名册: 找自己、刷血量滑条 (Players.Roster 与 GameEvents 互补)。
            int selfHp = -1, selfMaxHp = 0;
            try
            {
                var roster = Players.Roster();
                if (roster != null)
                {
                    for (int i = 0; i < roster.Count; i++)
                    {
                        var s = roster[i];
                        if (s.IsSelf)
                        {
                            _selfId = s.Id;
                            _selfKnown = true;
                            selfHp = s.Hp;
                            selfMaxHp = s.MaxHp;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (_hpSlider != null && selfHp >= 0 && selfMaxHp > 0)
            {
                _hpSlider.SetValue(selfHp * 100.0f / selfMaxHp);
                if (_hpLabel != null) _hpLabel.SetText("自己血量: " + selfHp + " / " + selfMaxHp);
            }

            string[] feed;
            ShopOffer shop;
            RelicOffer relic;
            ShopSettlement settle;
            string body;
            lock (_gate)
            {
                shop = _lastShopOffer;
                relic = _lastRelicOffer;
                settle = _lastSettlement;
                feed = _feed.ToArray();
                body = BuildBodyText(shop, relic, settle);
            }

            _body.SetText(body);

            // Badge: 最近事件类型 + 配色 (演示 SetText/SetColor)。
            if (_badgeText != null)
            {
                string badge = "等待";
                Color tint = new Color(0.35f, 0.38f, 0.45f, 0.95f);
                if (shop != null || settle != null || relic != null)
                {
                    badge = settle != null ? "成交" : (shop != null ? "商店" : "筹码");
                    tint = settle != null ? new Color(0.16f, 0.52f, 0.24f, 0.95f)
                          : new Color(0.85f, 0.55f, 0.15f, 0.95f);
                }
                _badgeText.SetText(badge);
                if (_badge != null) _badge.SetColor(tint);
            }
        }

        private string BuildBodyText(ShopOffer shop, RelicOffer relic, ShopSettlement settle)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("计数 商店报价" + _cShopOffer + " | 筹码候选" + _cRelicOffer +
                          " | 成交" + _cSettlement + " | 筹码结果" + _cRelicResult +
                          " | 刷新变化" + _cRerollChanged);
            sb.AppendLine("      血量变化" + _cHpChanged + " | 击杀" + _cKilled +
                          " | 旧:商店候选" + _cShopCand + " 筹码候选" + _cRelicCand + " 已选" + _cRelicSelected);
            sb.AppendLine(Rule());

            sb.AppendLine("[商店报价 ShopOffered]");
            sb.AppendLine(shop == null ? "(尚未收到——进一次卡牌商店就会有)" : FormatShopOffer(shop));
            sb.AppendLine("[筹码候选 RelicOffered]");
            sb.AppendLine(relic == null ? "(尚未收到——触发一次三选一就会有)" : FormatRelicOffer(relic));
            sb.AppendLine("[商店成交 ShopSettled]");
            sb.AppendLine(settle == null ? "(尚未成交)" : FormatSettlement(settle));
            sb.AppendLine("[筹码结果 RelicResult]");
            sb.AppendLine(FormatRelicResult());
            sb.AppendLine("[血量 / 击杀]");
            sb.AppendLine(FormatHpKill());

            string[] feed;
            lock (_gate) { feed = _feed.ToArray(); }
            sb.AppendLine("[事件流水 最近" + feed.Length + "条]");
            for (int i = 0; i < feed.Length; i++) sb.AppendLine(feed[i]);

            long self = _selfKnown ? _selfId : 0;
            string selfName = self != 0 ? Who(self) : "P?";
            int rerollPull = self != 0 ? GameEvents.RelicRerollRemaining(self) : -1;
            string rerollPush = "—";
            if (self != 0 && _rerollLeft.ContainsKey(self)) rerollPush = _rerollLeft[self].ToString();
            sb.AppendLine(Rule());
            sb.AppendLine("[拉取 downSn / 刷新余量]");
            sb.AppendLine("DownSn(5030 PVP成交)=" + GameEvents.DownSn(GameCommands.ShopBuy) +
                          " DownSn(5216 PVE成交)=" + GameEvents.DownSn(GameCommands.PveShopBuy) +
                          " DownSn(5212 筹码结果)=" + (GameEvents.DownSn(GameCommands.SelectRelic)));
            sb.AppendLine("刷新余量(" + selfName + ") 拉取=" + rerollPull + " 推送=" + rerollPush);
            sb.AppendLine("F9 显隐面板 · F8 清空 · 本面板由 WidgetService 绘制");
            return sb.ToString();
        }

        private string FormatShopOffer(ShopOffer offer)
        {
            return (offer.IsPve ? "PVE" : "PVP") + " pid=" + Who(offer.PlayerId) + " sn=" + offer.Sn + "\n" +
                   "  总价=" + offer.TotalGold + " (原价" + offer.Gold + " 折扣" + offer.DisCountGold + ")" +
                   " 离店=" + offer.IsClose + " 转账=" + offer.AssistPlayer + "(" + offer.AssistGold + ")\n" +
                   "  卡: " + JoinNames(offer.CardIds, Names.Card) + "\n" +
                   "  欲购槽位: " + JoinInts(offer.BuyCards) + " 已买: " + JoinBools(offer.AlreadyBought) +
                   " 免费: " + JoinBools(offer.Free) + " PVP免费卡=" + offer.FreeCard + "x" + offer.FreeCardNum;
        }

        private string FormatRelicOffer(RelicOffer offer)
        {
            return "pid=" + Who(offer.PlayerId) + " sn=" + offer.Sn + "\n" +
                   "  候选: " + JoinNames(offer.RelicIds, Names.Relic) + "\n" +
                   "  IsReroll=" + offer.IsReroll + " Index=" + offer.Index +
                   " Lv=" + offer.Lv + " SupLv=" + offer.SupLv + (offer.SupLv > 0 ? " (升星)" : "");
        }

        private string FormatSettlement(ShopSettlement settle)
        {
            int[] bought = settle.ResolveBoughtCardIds();
            string head = (settle.IsPve ? "PVE" : "PVP") + " pid=" + Who(settle.PlayerId) +
                          " downSn=" + settle.DownSn + " 离店=" + settle.IsClose +
                          " 转账=" + settle.AssistPlayer;
            if (bought == null || bought.Length == 0)
            {
                return head + "\n  (没买卡: 转账或离店)";
            }
            return head + "\n  买到: " + JoinNames(bought, Names.Card) +
                   "\n  槽位下标: " + JoinInts(settle.BuyCards);
        }

        private string FormatRelicResult()
        {
            if (_lastRelicId == 0) return "(尚未收到——第一次三选一选定/刷新就会出现)";
            return "pid=" + Who(_lastRelicPlayer) + " " + ( _lastRelicIsReroll ? "[刷新]" : "[选定]") +
                   " " + Names.Relic(_lastRelicId) +
                   " (id=" + _lastRelicId + ", 当时 downSn=" + _lastRelicDownSn + ")";
        }

        private string FormatHpKill()
        {
            var sb = new System.Text.StringBuilder();
            if (_lastHpPlayer != 0)
            {
                int sum;
                lock (_gate) { _hpDeltaSum.TryGetValue(_lastHpPlayer, out sum); }
                sb.Append("血量: ").Append(Who(_lastHpPlayer))
                  .Append(" 本次").Append(_lastHpDelta)
                  .Append(" (累计差值").Append(sum).Append(")")
                  .Append(" maxHp=").Append(_lastHpMax);
            }
            else
            {
                sb.Append("血量: (尚未收到)");
            }
            sb.Append("\n击杀: ");
            if (_lastVictim != 0)
            {
                sb.Append(Who(_lastAttacker)).Append(" 击杀 ").Append(Who(_lastVictim));
                int kc;
                lock (_gate) { _killCount.TryGetValue(_lastAttacker, out kc); }
                sb.Append(" (本局击杀").Append(kc).Append(")");
            }
            else
            {
                sb.Append("(尚未收到)");
            }
            return sb.ToString();
        }

        // =====================================================================
        // 事件处理器 (只收原始载荷, 名字在 OnUpdate 解析)
        // =====================================================================

        private void OnShopOffered(ShopOffer offer)
        {
            if (offer == null) return;
            lock (_gate)
            {
                _lastShopOffer = offer;
                _cShopOffer++;
                PushFeedNoLock("商店报价 " + (offer.IsPve ? "PVE" : "PVP") +
                               " sn=" + offer.Sn + " 卡数=" + (offer.CardIds == null ? 0 : offer.CardIds.Length));
            }
            Log.Info("ShopOffered: " + offer);
        }

        private void OnRelicOffered(RelicOffer offer)
        {
            if (offer == null) return;
            lock (_gate)
            {
                _lastRelicOffer = offer;
                _cRelicOffer++;
                PushFeedNoLock("筹码候选 sn=" + offer.Sn + " 候选数=" +
                               (offer.RelicIds == null ? 0 : offer.RelicIds.Length) +
                               " 刷新=" + offer.IsReroll + " 升星=" + offer.SupLv);
            }
            Log.Info("RelicOffered: " + offer);
        }

        private void OnShopSettled(ShopSettlement settle)
        {
            if (settle == null) return;
            lock (_gate)
            {
                _lastSettlement = settle;
                _cSettlement++;
                int[] bought = settle.ResolveBoughtCardIds();
                PushFeedNoLock("商店成交 " + (settle.IsPve ? "PVE" : "PVP") +
                               " downSn=" + settle.DownSn + " 买到数=" + (bought == null ? 0 : bought.Length));
            }
            Log.Info("ShopSettled: " + settle);
        }

        private void OnRelicResult(long playerId, int relicId, bool isReroll)
        {
            // downSn 必须在回调同步执行期间读 (消息还在队首), 离开就读不到。
            long downSn = 0;
            try { downSn = GameEvents.DownSn(GameCommands.SelectRelic); } catch { }

            lock (_gate)
            {
                _lastRelicPlayer = playerId;
                _lastRelicId = relicId;
                _lastRelicIsReroll = isReroll;
                _lastRelicDownSn = downSn;
                _cRelicResult++;
                PushFeedNoLock("筹码结果 " + (isReroll ? "[刷新]" : "[选定]") +
                               " relic=" + relicId + " downSn=" + downSn);
            }
            Log.Info("RelicResult pid=" + playerId + " relic=" + relicId + " isReroll=" + isReroll + " downSn=" + downSn);
        }

        private void OnRelicSelected(long playerId, int relicId)
        {
            lock (_gate)
            {
                _cRelicSelected++;
                PushFeedNoLock("旧事件 RelicSelected pid=" + playerId + " relic=" + relicId + " (应与 RelicResult 选定一致)");
            }
        }

        private void OnRelicRerollChanged(long playerId, int remaining)
        {
            lock (_gate)
            {
                _rerollLeft[playerId] = remaining;
                _cRerollChanged++;
                PushFeedNoLock("刷新余量变化 pid=" + playerId + " 剩=" + remaining);
            }
        }

        private void OnHeroHpChanged(long playerId, int delta, int maxHp)
        {
            lock (_gate)
            {
                _lastHpPlayer = playerId;
                _lastHpDelta = delta;
                _lastHpMax = maxHp;
                int sum;
                _hpDeltaSum.TryGetValue(playerId, out sum);
                _hpDeltaSum[playerId] = sum + delta;
                _cHpChanged++;
                PushFeedNoLock("血量 pid=" + playerId + " Δ=" + delta + " maxHp=" + maxHp);
            }
        }

        private void OnPlayerKilled(long victimId, long attackerId)
        {
            lock (_gate)
            {
                _lastVictim = victimId;
                _lastAttacker = attackerId;
                int kc;
                _killCount.TryGetValue(attackerId, out kc);
                _killCount[attackerId] = kc + 1;
                _cKilled++;
                PushFeedNoLock("击杀 " + attackerId + " -> " + victimId);
            }
            Log.Info("PlayerKilled victim=" + victimId + " attacker=" + attackerId);
        }

        private void OnShopCandidates(long playerId, System.Collections.Generic.IReadOnlyList<int> cardIds)
        {
            lock (_gate)
            {
                _cShopCand++;
                PushFeedNoLock("旧事件 ShopCandidates pid=" + playerId + " 数量=" + (cardIds == null ? 0 : cardIds.Count));
            }
        }

        private void OnRelicCandidates(long playerId, System.Collections.Generic.IReadOnlyList<int> relicIds)
        {
            lock (_gate)
            {
                _cRelicCand++;
                PushFeedNoLock("旧事件 RelicCandidates pid=" + playerId + " 数量=" + (relicIds == null ? 0 : relicIds.Count));
            }
        }

        // =====================================================================
        // 小工具
        // =====================================================================

        private static long UtcNowTicks()
        {
            return DateTime.UtcNow.Ticks;
        }

        private static string Rule()
        {
            return "────────────────────";
        }

        private void PushFeed(string line)
        {
            lock (_gate) { PushFeedNoLock(line); }
        }

        // 调用方必须已持有 _gate。
        private void PushFeedNoLock(string line)
        {
            _feed.Insert(0, "[" + DateTime.UtcNow.ToString("HH:mm:ss") + "] " + line);
            if (_feed.Count > 14) _feed.RemoveAt(_feed.Count - 1);
        }

        private string Who(long playerId)
        {
            if (playerId == 0) return "P?";
            string nick = null;
            try { nick = Players.Nick(playerId); } catch { }
            string name = string.IsNullOrEmpty(nick) ? ("P" + playerId) : nick;
            try
            {
                if (Players.IsSelf(playerId)) name += "(我)";
            }
            catch { }
            return name;
        }

        private static string JoinNames(int[] ids, Func<int, string> nameOf)
        {
            if (ids == null || ids.Length == 0) return "(无)";
            var parts = new string[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                try { parts[i] = nameOf(ids[i]); } catch { parts[i] = ids[i].ToString(); }
            }
            return string.Join("、", parts);
        }

        private static string JoinInts(int[] values)
        {
            if (values == null || values.Length == 0) return "(无)";
            return string.Join(",", values);
        }

        private static string JoinBools(bool[] values)
        {
            if (values == null || values.Length == 0) return "(无)";
            var parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++) parts[i] = values[i] ? "1" : "0";
            return string.Join(",", parts);
        }
    }
}
