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
        /// <summary>HUD searches allowed after each scene load; stops the idle 1 Hz full-tree walk.</summary>
        private const int SearchBudget = 12;
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
        private UIBattleInfoPanel _panel;
        private float _nextSearch, _nextProblem;
        private int _searchBudget = SearchBudget;
        private int _iconBudget;
        private bool _reportedDetail;
        private bool _cleared;
        private RelicIcon[] _selection;
        private bool[] _owned;
        private int[] _iconFailures;
        internal Action<string> OnShown;
        internal Action<string> OnProblem;


        internal void Tick(float now, Settings settings)
        {
            if (!ReferenceEquals(_selection, settings.Selection))
            {
                Reset();
                _searchBudget = SearchBudget;
                _selection = settings.Selection;
                _owned = new bool[_selection.Length];
                _iconFailures = new int[_selection.Length];
            }
            if (!settings.Enabled) { HideAll(); return; }
            if (_panel == null || _panel.isDisposed || _panel.parent == null)
            {
                // Searching is a full GRoot walk: only right after a scene load, never forever.
                if (now < _nextSearch || _searchBudget <= 0) return;
                _nextSearch = now + 1;
                _searchBudget--;
                Reset();
                _panel = FindPanel();
            }
            var players = _panel?.com_BattlePlayer;
            var container = players?.com_Container;
            if (container == null || !Visible(_panel) || !Visible(players)) { HideAll(); return; }
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
            if (player?.player == null || player.player.Id == 0 || gold == null || gold.isDisposed ||
                !Visible(button) || !Visible(info) || !Visible(gold))
            {
                Hide(index);
                return;
            }

            // Same public inventory used by the game's own relic information window.
            int count = RelicCatalog.FillOwned(_selection, player.GetRelicIds(), _owned);
            if (count == 0) { Hide(index); return; }
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
            if (scale <= 0f) { slot.Row.visible = false; return; }

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
                if (panel != null) { _search.Clear(); return panel; }
                int remaining = SearchLimit - visited - _search.Count;
                for (int i = node.numChildren - 1; i >= 0 && remaining-- > 0; i--)
                {
                    var child = node.GetChildAt(i) as GComponent;
                    if (child != null) _search.Add(child);
                }
            }
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

        /// <summary>A new scene gets a fresh search budget and clears the give-up flags.</summary>
        internal void OnSceneChanged()
        {
            Reset();
            _searchBudget = SearchBudget;
            _nextSearch = 0f;
        }

        internal void Reset()
        {
            for (int i = 0; i < _slots.Length; i++) Remove(i);
            if (_iconFailures != null)
                for (int i = 0; i < _iconFailures.Length; i++) _iconFailures[i] = 0;
            _panel = null;
            _cleared = false;
        }

        public void Dispose() { Reset(); }
    }
}
