using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Mods;
using Core.Unit;
using FairyGUI;
using GameLogic;
using PvePanelMod.Hands;
using PvePanelMod.Waves;
using Tools;
using UnityEngine;

namespace PvePanelMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new PanelController());
        }
    }

    /// <summary>
    /// PVE 面板（左侧信息栏 + 右侧情报栏）。只在 PVE 对局里显示（决定 9）；只读，不发送对局操作。
    /// 展开时把官方左栏调暗，免得两套信息叠在一起（草图里是「模糊」，这一版先用调暗，效果确认后再换）。
    /// </summary>
    public sealed class PanelController : ModBase
    {
        public override string Name => "PVE 面板";
        public override string Version => "0.2.0";
        public override string Author => "hajimi";

        private const float OfficialDimAlpha = 0f;   // 展开时直接隐藏官方左栏（两套信息叠在一起太乱）
        private Icons _icons;
        private LeftPanel _left;
        private RightPanel _right;
        private BattleView _battle;
        private HandDrawer _drawer;
        private Tooltip _tooltip;
        private HandService _hands;
        private int _lastHandRevision = -1;
        private bool _shown;
        private KeyCode _leftKey = KeyCode.F3, _handKey = KeyCode.F4, _rightKey = KeyCode.F5;
        private bool _rightHidden;
        private bool _leftCollapsed;
        private readonly BattleFeedReader _battleFeed = new BattleFeedReader();
        private readonly PinnedRelics _pinned = new PinnedRelics();
        /// <summary>商店、战斗窗口打开时自动收起左栏，关掉后再展开（配置 AutoCollapse，默认开）。</summary>
        private bool _autoCollapse = true;
        /// <summary>左栏是被「自动收起」收起来的（窗口关掉后要展开回来）；玩家自己点过就交给玩家，不再自动展开。</summary>
        private bool _autoCollapsed;
        /// <summary>这一次窗口打开已经处理过了（玩家手动展开后，不会在同一个窗口里又被收起）。</summary>
        private bool _busyHandled;
        private DateTime _idleSince = DateTime.MinValue;
        private bool _autoToggling;
        private RightState _rightState;
        private List<WaveInfo> _waves = new List<WaveInfo>();
        private string _wavesKey;
        private DateTime _wavesNext = DateTime.MinValue;
        private DateTime _next = DateTime.MinValue;
        private DateTime _nextError = DateTime.MinValue;
        /// <summary>最近一次渲染的玩家行（诊断日志用，只存引用）。</summary>
        private List<PlayerRow> _rows;
        /// <summary>配置里的总开关。默认关：mod 装了也不显示，改配置立即生效（热重载），不用重启游戏。</summary>
        private bool _enabled;
        private readonly List<GComponent> _dimmed = new List<GComponent>();
        private readonly Dictionary<GComponent, bool> _dimTouchable = new Dictionary<GComponent, bool>();

        public override void OnInitialize()
        {
            _icons = new Icons(Context?.Directory);
            _left = new LeftPanel(_icons);
            _right = new RightPanel(_icons);
            _battle = new BattleView();
            _drawer = new HandDrawer();
            _tooltip = new Tooltip();
            _hands = new HandService();
            ReadConfig(true);
            _rightState = new RightState();
            // 点了把手立刻刷新，不等下一个 0.25 秒（点击要跟手）
            _left.CollapsedChanged += () =>
            {
                // 自动收起 / 展开不写进配置：配置只记玩家自己选的状态
                if (!_autoToggling)
                {
                    _autoCollapsed = false;
                    _leftCollapsed = _left.Collapsed;
                    if (Config != null) { Config.Set(PanelConfigDocs.LeftPanelCollapsed, _leftCollapsed); Config.SaveIfDirty(); }
                }
                SetOfficialDim(!_left.Collapsed);
                _next = DateTime.MinValue;
            };
            _left.HandOpenChanged += () =>
            {
                Log.Info("PVE 面板手牌抽屉: " + (_left.HandOpen ? "打开" : "收起")
                    + (_left.HandOpen && _rows != null ? " · " + HandService.Describe(_rows) : ""));
                _next = DateTime.MinValue;
            };
            _left.ScaleRequested += dir =>
            {
                PanelScale.Value = PanelScale.Value + dir * PanelScale.Step;
                if (Config != null) { Config.Set(PanelConfigDocs.UiScale, Math.Round((double)PanelScale.Value, 1)); Config.SaveIfDirty(); }
                Log.Info("PVE 面板缩放: " + Mathf.RoundToInt(PanelScale.Value * 100) + "%");
                _next = DateTime.MinValue;
            };
            Context?.RegisterCleanup(Stop);
            Log.Info(_enabled
                ? "PVE 面板已启用：只在 PVE 对局里显示左右两栏"
                : "PVE 面板已加载：配置里 Enabled=false，暂不显示（改成 true 立即生效）");
        }

        public override void OnUnload() { Stop(); }

        public override void OnConfigChanged() { ReadConfig(false); }

        /// <summary>配置里的键名（如 "F3"）→ KeyCode；空、None、写错都当作不用这个键。</summary>
        private static KeyCode ParseKey(string name)
        {
            KeyCode k;
            if (string.IsNullOrWhiteSpace(name) || !Enum.TryParse(name.Trim(), true, out k)) return KeyCode.None;
            return k;
        }

        /// <summary>左栏底部的快捷键提示，跟着配置走；没设键的那一项不写。</summary>
        private string KeysLine()
        {
            var parts = new List<string>();
            if (_leftKey != KeyCode.None) parts.Add(_leftKey + " 左栏");
            if (_handKey != KeyCode.None) parts.Add(_handKey + " 手牌");
            if (_rightKey != KeyCode.None) parts.Add(_rightKey + " 右栏");
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>
        /// 快捷键（每帧看一次）。不响应的情况：游戏不在前台（SDK 可能读的是整个系统的键盘，切到浏览器打字也会被读到）、
        /// 正在聊天框里打字（照 ShopRelicPeekMod：GRoot 的焦点是输入框）、面板没在显示（大厅、PVP）。
        /// </summary>
        private void HandleHotkeys()
        {
            if (!_shown || !Application.isFocused) return;
            if (GRoot.inst != null && GRoot.inst.focus is GTextInput) return;
            if (Pressed(_leftKey)) _left.Toggle();
            if (Pressed(_handKey)) _left.ToggleHand();
            if (Pressed(_rightKey))
            {
                _rightHidden = !_rightHidden;
                // 记住：写进 config.json，下一局、重启游戏后保持
                if (Config != null) { Config.Set(PanelConfigDocs.RightPanelHidden, _rightHidden); Config.SaveIfDirty(); }
                _next = DateTime.MinValue;
            }
        }

        private static bool Pressed(KeyCode k)
        {
            if (k == KeyCode.None) return false;
            try { return InputService.IsKeyDown(k); } catch { return false; }
        }

        private void ReadConfig(bool initial)
        {
            bool service = false;
            string url = PanelConfigDocs.DefaultServiceUrl;
            if (Config != null)
            {
                if (initial)
                {
                    PanelConfigDocs.Apply(Config);
                    Config.SetDefault(PanelConfigDocs.Enabled, false);
                    Config.SetDefault(PanelConfigDocs.LeftToggleKey, PanelConfigDocs.DefaultLeftKey);
                    Config.SetDefault(PanelConfigDocs.HandToggleKey, PanelConfigDocs.DefaultHandKey);
                    Config.SetDefault(PanelConfigDocs.RightToggleKey, PanelConfigDocs.DefaultRightKey);
                    Config.SetDefault(PanelConfigDocs.RightPanelHidden, false);
                    Config.SetDefault(PanelConfigDocs.LeftPanelCollapsed, false);
                    Config.SetDefault(PanelConfigDocs.AutoCollapse, true);
                    Config.SetDefault(PanelConfigDocs.UiScale, 1.0);
                    Config.SetDefault(PanelConfigDocs.HandService, true);
                    Config.SetDefault(PanelConfigDocs.HandServiceUrl, PanelConfigDocs.DefaultServiceUrl);
                    Config.SaveIfDirty();
                }
                _enabled = Config.GetBool(PanelConfigDocs.Enabled, false);
                service = Config.GetBool(PanelConfigDocs.HandService, false);
                url = Config.GetString(PanelConfigDocs.HandServiceUrl, PanelConfigDocs.DefaultServiceUrl);
                _leftKey = ParseKey(Config.GetString(PanelConfigDocs.LeftToggleKey, PanelConfigDocs.DefaultLeftKey));
                _handKey = ParseKey(Config.GetString(PanelConfigDocs.HandToggleKey, PanelConfigDocs.DefaultHandKey));
                _rightKey = ParseKey(Config.GetString(PanelConfigDocs.RightToggleKey, PanelConfigDocs.DefaultRightKey));
                _rightHidden = Config.GetBool(PanelConfigDocs.RightPanelHidden, false);
                _leftCollapsed = Config.GetBool(PanelConfigDocs.LeftPanelCollapsed, false);
                _autoCollapse = Config.GetBool(PanelConfigDocs.AutoCollapse, true);
                PanelScale.Value = Config.GetFloat(PanelConfigDocs.UiScale, 1f);
            }
            _left?.SetKeysLine(KeysLine());
            _autoCollapsed = false;   // 配置一改就以配置为准
            _left?.SetCollapsed(_leftCollapsed);
            // 面板总开关关着时连查询器都不用建：不联网、不订阅请求逻辑
            _hands?.Configure(_enabled && service, url);
            Log.Info("PVE 面板配置: 面板 " + (_enabled ? "开" : "关") + "，第三方手牌服务 " + (service ? "开" : "关"));
        }

        public override void OnUpdate()
        {
            if (!_enabled)
            {
                // 关着：把已经画出来的东西收掉就好，别继续每帧读游戏数据
                if (_shown || _dimmed.Count > 0) HideAll();
                return;
            }
            // 每帧：手牌抽屉跟着左栏的滑动动画挪位置（只改坐标，很省）
            if (_drawer != null && _drawer.Visible && _left != null)
                _drawer.SetX(Math.Max(0f, _left.TabsRight) + 10f * PanelScale.K());
            try { HandleHotkeys(); } catch { }
            // 每帧：悬停小字（只看鼠标位置，不挡点击）
            try { if (_left != null && _shown) _tooltip?.Tick(); } catch { }
            if (DateTime.UtcNow < _next) return;
            _next = DateTime.UtcNow.AddSeconds(0.25);
            try
            {
                if (!PanelData.ShouldShow()) { HideAll(); return; }
                var official = PanelData.FindOfficialRows();
                var rows = PanelData.Read(PanelData.ReadOfficialHeads(official));
                _rows = rows;
                if (rows.Count == 0) { HideAll(); return; }
                bool pinned = _pinned.Installed;
                if (pinned)
                {
                    var shown = _pinned.Read(PanelData.FindPanel());
                    foreach (var r in rows) { List<int> ids; if (shown.TryGetValue(r.Id, out ids)) r.Pinned = ids; }
                }
                _left.Render(rows, pinned);
                _shown = true;
                AutoCollapse();
                RenderBattle();
                RenderHands(rows);
                RenderRight(rows);
                RefreshDimTargets(official);
            }
            catch (Exception ex)
            {
                HideAll();
                if (DateTime.UtcNow >= _nextError)
                {
                    _nextError = DateTime.UtcNow.AddSeconds(15);
                    Log.Warn("PVE 面板暂时画不出来：" + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 商店、战斗窗口打开 → 收起左栏（它挡住窗口）；关掉 → 展开回来。
        /// 只在窗口刚打开那一下收一次：玩家在窗口里自己又展开了，就不再收。
        /// 原本就是收起的，窗口关掉后也不会被展开。商店和战斗连着开时中间可能有一瞬间都没开，等 0.6 秒再展开，免得闪一下。
        /// </summary>
        private void AutoCollapse()
        {
            // 只管自己的：别人进店、别人打架时官方也会弹出同一个窗口，那时不收
            // 转账也算：从商店点转账时商店窗口先关、转账窗口再开，中间不能把左栏展开回来
            bool busy = _autoCollapse && (GameWindows.ShopOpen(_rightState.LastShopper) || GameWindows.AtmOpen() || GameWindows.FightOpen());
            var now = DateTime.UtcNow;
            if (!busy)
            {
                if (_idleSince == DateTime.MinValue) _idleSince = now;
                if ((now - _idleSince).TotalSeconds < 0.6) return;
                _busyHandled = false;
                if (_autoCollapsed && _left.Collapsed) AutoToggle();
                _autoCollapsed = false;
                return;
            }
            _idleSince = DateTime.MinValue;
            if (_busyHandled) return;
            _busyHandled = true;
            if (!_left.Collapsed) { AutoToggle(); _autoCollapsed = true; }
        }

        private void AutoToggle()
        {
            _autoToggling = true;
            try { _left.Toggle(); } finally { _autoToggling = false; }
        }

        /// <summary>
        /// 战斗面板：读战斗胜率助手的结果（没装就什么都没有），挂在左栏下面。
        /// 左栏收起时照样显示（战斗时左栏会自动收起，这时最需要看胜率），位置不变。
        /// </summary>
        private void RenderBattle()
        {
            var odds = _battleFeed.Read();
            if (odds == null) { _battle.Hide(); return; }
            _battle.Render(odds, _left.BottomY + 10f * PanelScale.K());
            _battleFeed.Claim();
        }

        /// <summary>手牌抽屉：粉色把手打开时显示，贴在左栏（含把手）右边，行和左栏对齐；左栏收起时贴到屏幕边并加楼层色条。</summary>
        private void RenderHands(List<PlayerRow> rows)
        {
            var hands = _hands.Tick(rows, _left.HandOpen);
            if (_hands.Revision != _lastHandRevision)
            {
                _lastHandRevision = _hands.Revision;
                if (!string.IsNullOrEmpty(_hands.LastStatus)) Log.Info("PVE 面板手牌服务: " + _hands.LastStatus);
            }
            if (!_left.HandOpen) { _drawer.Hide(); return; }
            float k = PanelScale.K();
            _drawer.Render(Math.Max(0f, _left.TabsRight) + 10f * k, _left.SideTop, _left.SideHeight, _left.RowTops(), _left.RowHeight,
                rows, hands, _left.Collapsed);
        }

        private void RenderRight(List<PlayerRow> rows)
        {
            _rightState.CheckMatch();
            var gm = SimpleSingletonProvider<GameLogicManager>.inst;
            var room = gm?.room?.curRoomInfo;
            if (room == null || _rightHidden) { _right.Hide(); return; }
            int round = room.Round;
            // 波次表只在换图、换难度、换回合时重算（另外每 10 秒兜底刷新一次）
            string key = room.MapId + "|" + room.Difficulty + "|" + round;
            if (key != _wavesKey || DateTime.UtcNow >= _wavesNext)
            {
                _wavesKey = key;
                _wavesNext = DateTime.UtcNow.AddSeconds(10);
                try { _waves = WaveSchedule.Build(room, SimpleSingletonProvider<LandManager>.inst, 20); }
                catch { _waves = new List<WaveInfo>(); }
            }
            _right.Render(RightEdge(), round, _waves, rows, _rightState.Snapshot());
        }

        /// <summary>
        /// 屏幕右边在界面坐标里的位置。窄屏（如 1028×720）时 GRoot 和屏幕不一样宽，右栏会离开屏幕边；
        /// 官方战斗面板 UIBattleInfoPanel 自己是贴着屏幕铺满的，用它的右边界最准。
        /// </summary>
        private static float RightEdge()
        {
            var root = GRoot.inst;
            var panel = PanelData.FindPanel();
            return panel != null && panel.width > 1f ? panel.x + panel.width : root.width;
        }

        /// <summary>官方行可能被游戏重建，每次都把当前的官方行调暗（展开时）。</summary>
        private void RefreshDimTargets(List<GComponent> official)
        {
            bool dim = !_left.Collapsed;
            foreach (var r in official)
            {
                if (r == null || r.isDisposed) continue;
                if (!_dimmed.Contains(r))
                {
                    _dimmed.Add(r);
                    _dimTouchable[r] = r.touchable;   // 记原值：官方行原本也可能是不可点的
                }
                ApplyDim(r, dim);
            }
            PruneDimmed();
        }

        private void SetOfficialDim(bool dim)
        {
            foreach (var r in _dimmed)
                if (r != null && !r.isDisposed) ApplyDim(r, dim);
            PruneDimmed();
        }

        /// <summary>
        /// 调暗 / 恢复官方左栏。透明的同时必须关掉它的点击：本面板整块 touchable=false，点击会穿到下面那些
        /// 看不见的官方按钮上（点头像位置会弹出官方玩家信息窗）。恢复时按记下的原值还原。
        /// </summary>
        private void ApplyDim(GComponent r, bool dim)
        {
            float a = dim ? OfficialDimAlpha : 1f;
            if (Math.Abs(r.alpha - a) > 0.001f) r.alpha = a;
            bool touchable;
            if (dim) touchable = false;
            else if (!_dimTouchable.TryGetValue(r, out touchable)) touchable = true;
            if (r.touchable != touchable) r.touchable = touchable;
        }

        private void PruneDimmed()
        {
            for (int i = _dimmed.Count - 1; i >= 0; i--)
            {
                var r = _dimmed[i];
                if (r != null && !r.isDisposed) continue;
                _dimmed.RemoveAt(i);
                if (r != null) _dimTouchable.Remove(r);
            }
        }

        private void HideAll()
        {
            _left?.Hide();
            _right?.Hide();
            _battle?.Hide();
            _drawer?.Hide();
            _tooltip?.Hide();
            _shown = false;
            SetOfficialDim(false);
        }

        private void Stop()
        {
            SetOfficialDim(false);
            _dimmed.Clear();
            _dimTouchable.Clear();
            _left?.Dispose();
            _right?.Dispose();
            _battle?.Dispose();
            _drawer?.Dispose();
            _tooltip?.Dispose();
            _hands?.Dispose();
            _rightState?.Dispose();
            _icons?.Dispose();
        }
    }
}
