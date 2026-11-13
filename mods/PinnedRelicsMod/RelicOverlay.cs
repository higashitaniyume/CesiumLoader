using System;
using System.Collections.Generic;
using FairyGUI;
using UI;

namespace PinnedRelicsMod
{
    internal sealed class RelicOverlay : IDisposable
    {
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
        private float _nextSearch;
        private bool _cleared;
        private RelicIcon[] _selection;
        private bool[] _owned;
        internal Action<string> OnShown;


        internal void Tick(float now, Settings settings)
        {
            if (!ReferenceEquals(_selection, settings.Selection))
            {
                Reset();
                _selection = settings.Selection;
                _owned = new bool[_selection.Length];
            }
            if (!settings.Enabled) { HideAll(); return; }
            if (_panel == null || _panel.isDisposed || _panel.parent == null)
            {
                if (now < _nextSearch) return;
                _nextSearch = now + 1;
                Reset();
                _panel = FindPanel();
            }
            var players = _panel?.com_BattlePlayer;
            var container = players?.com_Container;
            if (container == null || !Visible(_panel) || !Visible(players)) { HideAll(); return; }
            RefreshSlot(0, container.com_Player_1, settings);
            RefreshSlot(1, container.com_Player_2, settings);
            RefreshSlot(2, container.com_Player_3, settings);
            RefreshSlot(3, container.com_Player_4, settings);
        }

        private void RefreshSlot(int index, UIBattleInfo_Button_PlayerInfo button, Settings settings)
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

            int column = 0;
            bool loaded = true;
            for (int i = 0; i < _selection.Length; i++)
            {
                bool owned = _owned[i];
                if (!owned) { if (slot.Icons[i] != null) slot.Icons[i].View.visible = false; continue; }
                EnsureIcon(slot, i);
                slot.Icons[i].View.visible = true;
                slot.Icons[i].View.SetXY(column++ * (settings.Size + settings.Gap), 0);
                slot.Icons[i].Layout(settings.Size);
                if (!slot.Icons[i].IsLoaded) loaded = false;
            }
            slot.Row.visible = scale > 0;
            if (slot.Row.visible && loaded && !slot.ReportedShown)
            {
                slot.ReportedShown = true;
                OnShown?.Invoke("玩家栏 " + (index + 1) + " 已创建并显示 " + count + " 个持有筹码图标（游戏原生资源）。");
            }
            _cleared = false;
        }

        private void EnsureIcon(Slot slot, int index)
        {
            if (slot.Icons[index] != null && !slot.Icons[index].View.isDisposed) return;
            var icon = new GameRelicIcon(_selection[index].Id);
            try { slot.Row.AddChild(icon.View); }
            catch { icon.Dispose(); throw; }
            slot.Icons[index] = icon;
        }

        private UIBattleInfoPanel FindPanel()
        {
            _search.Clear();
            var root = GRoot.inst;
            if (root == null) return null;
            _search.Add(root);
            int visited = 0;
            while (_search.Count > 0 && visited++ < 4096)
            {
                int last = _search.Count - 1;
                var node = _search[last];
                _search.RemoveAt(last);
                if (node == null || node.isDisposed) continue;
                var panel = node as UIBattleInfoPanel;
                if (panel != null) { _search.Clear(); return panel; }
                int remaining = 4096 - visited - _search.Count;
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

        internal void Reset()
        {
            for (int i = 0; i < _slots.Length; i++) Remove(i);
            _panel = null;
            _cleared = false;
        }

        public void Dispose() { Reset(); }
    }
}
