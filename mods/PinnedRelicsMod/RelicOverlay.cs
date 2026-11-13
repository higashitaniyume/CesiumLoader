using System;
using System.Collections.Generic;
using FairyGUI;
using UI;

namespace PinnedRelicsMod
{
    internal sealed class RelicOverlay : IDisposable
    {
        /// <summary>Single HUD search node cap.</summary>
        private const int SearchLimit = 4096;
        /// <summary>Icons built per tick, so a fresh scene never builds every row in one frame.</summary>
        private const int IconsPerTick = 2;
        /// <summary>Give up on a relic whose icon keeps failing (the game may have changed its config).</summary>
        private const int MaxIconFailures = 3;

        private sealed class Slot
        {
            internal UICom_PlayerInfo Info;
            internal GComponent Row;
            internal readonly GameRelicIcon[] Icons;
            internal Slot(int count)
            {
                Icons = new GameRelicIcon[count];
            }
            internal long PlayerId;
            internal bool ReportedShown;
        }

        private readonly Slot[] _slots = new Slot[4];
        private readonly List<GComponent> _search = new List<GComponent>();
        /// <summary>Last logged diagnostic state per slot, so a steady frame logs nothing.</summary>
        private readonly long[] _diag = new long[4];
        private UIBattleInfoPanel _panel;
        private float _nextSearch, _nextProblem, _nextPanelDiag;
        private int _iconBudget, _searches, _visited;
        private long _panelDiag = -1;
        private bool _reportedDetail;
        private bool _cleared;
        private RelicIcon[] _selection;
        private bool[] _owned;
        private int[] _iconFailures;
        internal Action<string> OnShown;
        internal Action<string> OnProblem;
        internal Action<string> OnDiag;


        internal void Tick(float now, Settings settings)
        {
            if (!ReferenceEquals(_selection, settings.Selection))
            {
                Reset();
                _selection = settings.Selection;
                _owned = new bool[_selection.Length];
                _iconFailures = new int[_selection.Length];
            }
            if (!settings.Enabled) { HideAll(); return; }
            if (_panel == null || _panel.isDisposed || _panel.parent == null)
            {
                // The HUD appears a moment after the scene load and is rebuilt on re-entry:
                // keep looking at 1 Hz while it is missing (a full GRoot walk is cheap).
                if (now < _nextSearch) return;
                _nextSearch = now + 1;
                _searches++;
                _visited = 0;
                _panel = FindPanel();
                if (_panel != null)
                {
                    if (_panelDiag != 1)
                    {
                        _panelDiag = 1;
                        OnDiag?.Invoke("已定位战斗 HUD（第 " + _searches + " 次搜索，访问 " + _visited + " 个节点）");
                    }
                }
                else if (_panelDiag != 0 || now >= _nextPanelDiag)
                {
                    // One line when it goes missing, then every 30 s so the lobby stays quiet.
                    _panelDiag = 0;
                    _nextPanelDiag = now + 30;
                    OnDiag?.Invoke("未定位战斗 HUD（第 " + _searches + " 次搜索，访问 " + _visited + " 个节点）");
                }
            }
            var players = _panel?.com_BattlePlayer;
            var container = players?.com_Container;
            if (container == null)
            {
                HideAll();
                for (int i = 0; i < _slots.Length; i++) Diag(i, 2, now);
                return;
            }
            if (!Visible(_panel))
            {
                HideAll();
                for (int i = 0; i < _slots.Length; i++) Diag(i, 3, now);
                return;
            }
            if (!Visible(players))
            {
                HideAll();
                for (int i = 0; i < _slots.Length; i++) Diag(i, 4, now);
                return;
            }
            // One slot must never take the other three (and the whole row set) down with it.
            _iconBudget = IconsPerTick;
            RefreshSlot(0, container.com_Player_1, settings, now);
            RefreshSlot(1, container.com_Player_2, settings, now);
            RefreshSlot(2, container.com_Player_3, settings, now);
            RefreshSlot(3, container.com_Player_4, settings, now);
        }

        private void RefreshSlot(int index, UIBattleInfo_Button_PlayerInfo button, Settings settings, float now)
        {
            try { RefreshSlotCore(index, button, settings, now); }
            catch (Exception exception)
            {
                Hide(index);
                Report(now, "玩家栏 " + (index + 1) + " 刷新失败", exception);
            }
        }

        private void RefreshSlotCore(int index, UIBattleInfo_Button_PlayerInfo button, Settings settings, float now)
        {
            var player = button?.PlayerData;
            var info = button?.com_player as UICom_PlayerInfo;
            var gold = info?.txt_Gold;
            if (player == null) { Hide(index); Diag(index, 10, now); return; }
            if (player.player == null) { Hide(index); Diag(index, 11, now); return; }
            if (player.player.Id == 0) { Hide(index); Diag(index, 12, now); return; }
            if (gold == null) { Hide(index); Diag(index, 13, now); return; }
            if (gold.isDisposed) { Hide(index); Diag(index, 14, now); return; }
            if (!Visible(button)) { Hide(index); Diag(index, 15, now); return; }
            if (!Visible(info)) { Hide(index); Diag(index, 16, now); return; }
            if (!Visible(gold)) { Hide(index); Diag(index, 17, now); return; }

            // Same public inventory used by the game's own relic information window.
            int count = RelicCatalog.FillOwned(_selection, player.GetRelicIds(), _owned);
            if (count == 0) { Hide(index); Diag(index, 18, now); return; }
            var slot = _slots[index];
            if (slot == null || slot.Row.isDisposed || slot.Row.parent != info ||
                !ReferenceEquals(slot.Info, info) || slot.PlayerId != player.player.Id)
            {
                Remove(index);
                slot = new Slot(_selection.Length) { Info = info, PlayerId = player.player.Id,
                    Row = new GComponent { name = "PinnedRelicsMod.Row", touchable = false, sortingOrder = 100 } };
                _slots[index] = slot;
                info.AddChild(slot.Row);
            }

            // The anchor's local coordinates are converted into its actual owner.
            // No 1920x1080 screen offsets: rows inherit avatar motion and UI scaling.
            var anchor = info.GlobalToLocal(gold.LocalToGlobal(new UnityEngine.Vector2(0, gold.height)));
            float x = Math.Max(0, anchor.x + settings.X);
            float y = anchor.y + settings.Y;
            float available = Math.Max(0, info.width - x - 4);
            float scale = RelicCatalog.FitScale(count, settings.Size, settings.Gap, available);
            slot.Row.SetXY(x, y);
            slot.Row.SetScale(scale, scale);
            slot.Row.SetSize(count * settings.Size + (count - 1) * settings.Gap, settings.Size);
            _cleared = false;
            // Nothing fits: no point building icons that would render at zero size.
            if (scale <= 0f) { slot.Row.visible = false; DiagRow(index, slot, now, count, 0, false, scale, available, false); return; }

            int column = 0;
            bool loaded = true;
            for (int i = 0; i < _selection.Length; i++)
            {
                bool owned = _owned[i];
                if (!owned) { if (slot.Icons[i] != null) slot.Icons[i].View.visible = false; continue; }
                if (!HasIcon(slot, i))
                {
                    // Skip before spending the budget, otherwise one dead relic starves the rest.
                    if (_iconFailures[i] >= MaxIconFailures) { loaded = false; continue; }
                    if (_iconBudget <= 0) { loaded = false; continue; }
                    _iconBudget--;
                    if (!EnsureIcon(slot, i, now)) { loaded = false; continue; }
                }
                slot.Icons[i].View.visible = true;
                slot.Icons[i].View.SetXY(column++ * (settings.Size + settings.Gap), 0);
                slot.Icons[i].Layout(settings.Size);
                if (!slot.Icons[i].IsLoaded) loaded = false;
            }
            slot.Row.visible = true;
            DiagRow(index, slot, now, count, column, loaded, scale, available, true);
            if (column > 0 && loaded && !slot.ReportedShown)
            {
                slot.ReportedShown = true;
                OnShown?.Invoke("玩家栏 " + (index + 1) + " 已创建并显示 " + column + " 个持有筹码图标（游戏原生资源）。");
            }
        }

        private static bool HasIcon(Slot slot, int index)
        {
            var icon = slot.Icons[index];
            return icon != null && !icon.View.isDisposed;
        }

        private bool EnsureIcon(Slot slot, int index, float now)
        {
            if (_iconFailures[index] >= MaxIconFailures) return false;
            GameRelicIcon icon = null;
            try
            {
                icon = new GameRelicIcon(_selection[index].Id);
                slot.Row.AddChild(icon.View);
                slot.Icons[index] = icon;
                _iconFailures[index] = 0;
                return true;
            }
            catch (Exception exception)
            {
                if (icon != null) icon.Dispose();
                slot.Icons[index] = null;
                int failures = ++_iconFailures[index];
                if (failures == 1)
                    Report(now, "筹码「" + _selection[index].Name + "」图标创建失败，稍后重试", exception);
                else if (failures == MaxIconFailures)
                    Report(now, "筹码「" + _selection[index].Name + "」图标创建失败，本次对局跳过", exception);
                return false;
            }
        }

        /// <summary>Row state, logged only when it changes.</summary>
        private void DiagRow(int index, Slot slot, float now, int owned, int shown, bool loaded, float scale, float available, bool visible)
        {
            long state = 17;
            state = state * 31 + owned;
            state = state * 31 + shown;
            state = state * 31 + (loaded ? 1 : 0);
            state = state * 31 + (visible ? 1 : 0);
            state = state * 31 + (int)(scale * 1000);
            state = state * 31 + (int)available;
            if (state == _diag[index]) return;
            _diag[index] = state;
            OnDiag?.Invoke("玩家栏 " + (index + 1) + "：匹配 " + owned + "，图标 " + shown + "，" +
                (loaded ? "纹理就绪" : "纹理未就绪") + "，行缩放 " + scale.ToString("F2") +
                "，可用宽 " + available.ToString("F0") + "，行可见 " + (visible ? "是" : "否"));
        }

        /// <summary>Hidden-slot reason, logged only when it changes.</summary>
        private void Diag(int index, long code, float now)
        {
            if (_diag[index] == code) return;
            _diag[index] = code;
            string reason;
            switch (code)
            {
                case 2: reason = "未取到玩家栏容器"; break;
                case 3: reason = "HUD 面板不可见"; break;
                case 4: reason = "玩家栏容器不可见"; break;
                case 10: reason = "没有玩家数据"; break;
                case 11: reason = "玩家对象为空"; break;
                case 12: reason = "玩家 Id 为 0"; break;
                case 13: reason = "星币文本为空"; break;
                case 14: reason = "星币文本已销毁"; break;
                case 15: reason = "玩家栏按钮不可见"; break;
                case 16: reason = "玩家信息组件不可见"; break;
                case 17: reason = "星币文本不可见"; break;
                case 18: reason = "持有筹码中没有名单内的项"; break;
                default: reason = "跳过(" + code + ")"; break;
            }
            OnDiag?.Invoke("玩家栏 " + (index + 1) + "：" + reason);
        }

        private void Report(float now, string what, Exception exception)
        {
            if (now < _nextProblem) return;
            _nextProblem = now + 15;
            var error = exception.GetBaseException();
            // The first report keeps the stack; later ones stay short so the log does not flood.
            OnProblem?.Invoke(what + "：" + (_reportedDetail ? error.Message : error.ToString()));
            _reportedDetail = true;
        }

        private UIBattleInfoPanel FindPanel()
        {
            _search.Clear();
            var root = GRoot.inst;
            if (root == null) return null;
            _search.Add(root);
            int visited = 0;
            while (_search.Count > 0 && visited++ < SearchLimit)
            {
                int last = _search.Count - 1;
                var node = _search[last];
                _search.RemoveAt(last);
                if (node == null || node.isDisposed) continue;
                var panel = node as UIBattleInfoPanel;
                if (panel != null) { _visited = visited; _search.Clear(); return panel; }
                int remaining = SearchLimit - visited - _search.Count;
                for (int i = node.numChildren - 1; i >= 0 && remaining-- > 0; i--)
                {
                    var child = node.GetChildAt(i) as GComponent;
                    if (child != null) _search.Add(child);
                }
            }
            _visited = visited;
            _search.Clear();
            return null;
        }

        private static bool Visible(GObject obj)
        {
            // Also checks an avatar reparented to the target-selection container.
            int depth = 0;
            for (var current = obj; current != null && depth++ < 32; current = current.parent)
                if (current.isDisposed || !current.visible || current.scaleX == 0 || current.scaleY == 0) return false;
            return obj != null && obj.parent != null;
        }

        internal void HideAll()
        {
            if (_cleared) return;
            for (int i = 0; i < _slots.Length; i++) Hide(i);
            _cleared = true;
        }

        private void Hide(int index)
        {
            var row = _slots[index]?.Row;
            if (row != null && !row.isDisposed) row.visible = false;
        }

        private void Remove(int index)
        {
            var slot = _slots[index];
            _slots[index] = null;
            if (slot != null && !slot.Row.isDisposed) slot.Row.Dispose();
        }

        /// <summary>A new scene gets a fresh search budget and drops per-scene state.</summary>
        internal void OnSceneChanged()
        {
            Reset();
            _nextSearch = 0f;
            _nextPanelDiag = 0f;
            _searches = 0;
            _panelDiag = -1;
        }

        internal void Reset()
        {
            for (int i = 0; i < _slots.Length; i++) Remove(i);
            for (int i = 0; i < _diag.Length; i++) _diag[i] = -1;
            if (_iconFailures != null)
                for (int i = 0; i < _iconFailures.Length; i++) _iconFailures[i] = 0;
            _panel = null;
            _cleared = false;
        }

        public void Dispose() { Reset(); }
    }
}
