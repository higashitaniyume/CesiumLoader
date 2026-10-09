using System;
using System.Collections.Generic;
using FairyGUI;
using UI;
using Tools;
using UnityEngine;
using CesiumLoader.SDK.Logging;

namespace HandViewerMod
{
    internal sealed class HandViewerUi : IDisposable
    {
        sealed class StripComponent : GComponent
        {
            public StripComponent() { SetupOverflow(OverflowType.Hidden); }
        }
        sealed class Binding
        {
            public UIBattleInfo_Button_PlayerInfo Owner;
            public readonly HandStripModel Model = new HandStripModel();
            public GComponent Button, Strip, Content;
            public GTextField Arrow;
            public Color ButtonColor;
            public string Signature;
            public float Height, ContentWidth, Offset;
            public bool Anchored;
            public Vector2 HeadTop, HeadBottom, StarRight;
            public string LastRenderError;
            public float PanelWidth, PanelHeight;
        }
        readonly List<Binding> bindings = new List<Binding>();
        readonly HandQueryController query;
        string lastDiagnostic;
        public string UnavailableReason { get; set; }
        public HandViewerUi(HandQueryController query) { this.query = query; }
        void Diagnose(string message)
        {
            if (lastDiagnostic == message) return;
            lastDiagnostic = message; SdkLog.Info("HandViewer", "[hud] " + message);
        }
        static GTextField Text(string text, float width, float height, int size)
        {
            var label = new GTextField { text = text, touchable = false };
            label.SetSize(width, height);
            label.textFormat.size = size; label.textFormat.color = Color.white;
            label.textFormat = label.textFormat;
            return label;
        }
        public void Poll()
        {
            var root = GRoot.inst;
            var panel = Find(root, 0);
            var container = panel?.com_BattlePlayer?.com_Container;
            if (container == null)
            {
                foreach (var binding in bindings) { binding.Button.visible = false; binding.Strip.visible = false; }
                Diagnose("等待主 HUD 玩家容器"); return;
            }
            var owners = new[] { container.com_Player_1, container.com_Player_2, container.com_Player_3, container.com_Player_4 };
            bool rebuild = bindings.Count != owners.Length;
            if (!rebuild)
                for (int i = 0; i < owners.Length; i++)
                    if (!ReferenceEquals(bindings[i].Owner, owners[i]) || bindings[i].Button.isDisposed
                        || bindings[i].Button.parent != panel) rebuild = true;
            if (rebuild)
            {
                var opened = new List<long>();
                foreach (var binding in bindings) if (binding.Model.Open) opened.Add(binding.Model.PlayerId);
                ClearBindings();
                var colors = new[] { new Color(0.9f, 0.18f, 0.2f), new Color(0.18f, 0.75f, 0.3f),
                    new Color(0.15f, 0.4f, 0.95f), new Color(1f, 0.82f, 0.12f) };
                for (int slot = 0; slot < owners.Length; slot++)
                {
                    var owner = owners[slot];
                    if (owner == null) continue;
                    var binding = new Binding { Owner = owner, ButtonColor = colors[slot] };
                    binding.Model.Bind(owner.PlayerData?.player.Id ?? 0);
                    if (opened.Contains(binding.Model.PlayerId)) binding.Model.Toggle();
                    binding.Button = new GComponent { opaque = true }; binding.Button.SetSize(28, 32);
                    binding.Arrow = Text(">", 28, 32, 26);
                    binding.Arrow.stroke = 2;
                    binding.Arrow.strokeColor = Color.black;
                    binding.Arrow.textFormat.bold = true;
                    binding.Arrow.autoSize = AutoSizeType.None;
                    binding.Arrow.align = AlignType.Center; binding.Arrow.verticalAlign = VertAlignType.Middle;
                    binding.Button.AddChild(binding.Arrow);
                    binding.Strip = new StripComponent { opaque = true };
                    binding.Content = new GComponent(); binding.Strip.AddChild(binding.Content);
                    binding.Button.onClick.Add(context =>
                    {
                        context.StopPropagation();
                        bool opened = binding.Model.Toggle();
                        SdkLog.Info("HandViewer", "[hud] 三角点击: slot=" + bindings.IndexOf(binding) + " open=" + opened);
                        if (opened && string.IsNullOrEmpty(UnavailableReason)) query.Refresh(DateTime.UtcNow.Ticks / 10000);
                        binding.Strip.visible = binding.Model.Open;
                    });
                    binding.Strip.onClick.Add(context => context.StopPropagation());
                    binding.Strip.AddEventListener("onMouseWheel", context =>
                    {
                        context.StopPropagation();
                        binding.Offset = Math.Max(0, Math.Min(Math.Max(0, binding.ContentWidth - binding.Strip.width),
                            binding.Offset + context.inputEvent.mouseWheelDelta * -48));
                        binding.Content.x = -binding.Offset;
                    });
                    panel.AddChild(binding.Button); panel.AddChild(binding.Strip); bindings.Add(binding);
                }
            }
            bool hudVisible = panel.visible && panel.scaleX != 0 && panel.scaleY != 0
                && panel.com_BattlePlayer.visible && panel.com_BattlePlayer.scaleX != 0
                && panel.com_BattlePlayer.scaleY != 0 && panel.com_BattlePlayer.touchableAll;
            foreach (var binding in bindings)
            {
                long id = binding.Owner.PlayerData?.player.Id ?? 0;
                if (binding.Model.PlayerId != id) { binding.Model.Bind(id); binding.Signature = null; }
                var info = binding.Owner.com_player as UICom_PlayerInfo;
                bool visible = hudVisible && id != 0 && binding.Owner.visible && binding.Owner.scaleX != 0
                    && binding.Owner.scaleY != 0 && info?.com_Head != null && info.list_Level != null;
                binding.Button.visible = visible;
                binding.Strip.visible = visible && binding.Model.Open;
                if (!visible) continue;
                var head = info.com_Head;
                // Capture child layout once: hover/selection animations must not reposition our controls.
                if (!binding.Anchored || binding.PanelWidth != panel.width || binding.PanelHeight != panel.height)
                {
                    binding.PanelWidth = panel.width; binding.PanelHeight = panel.height;
                    binding.HeadTop = panel.GlobalToLocal(head.LocalToGlobal(Vector2.zero));
                    binding.HeadBottom = panel.GlobalToLocal(head.LocalToGlobal(new Vector2(head.width, head.height)));
                    var stars = info.list_Level;
                    binding.StarRight = panel.GlobalToLocal(stars.LocalToGlobal(new Vector2(stars.width, 0)));
                    binding.Anchored = true;
                }
                var top = binding.HeadTop;
                var bottom = binding.HeadBottom;
                var starRight = binding.StarRight;
                float height = Math.Max(1, Math.Abs(bottom.y - top.y));
                float x = Math.Max(bottom.x, starRight.x) + 4;
                float buttonSize = height / 2;
                binding.Button.SetSize(buttonSize, buttonSize);
                binding.Arrow.SetSize(buttonSize, buttonSize);
                binding.Arrow.textFormat.size = Math.Max(12, (int)(buttonSize * 0.7f));
                binding.Button.SetXY(x, top.y + (height - buttonSize) / 2);
                binding.Strip.SetXY(x + buttonSize + 4, top.y);
                var screenRight = panel.GlobalToLocal(root.LocalToGlobal(new Vector2(root.width - 8, 0)));
                float available = Math.Max(1, screenRight.x - binding.Strip.x);
                PlayerHand hand = null;
                if (query.Snapshot != null)
                    foreach (var player in query.Snapshot.players) if (player.playerId == id.ToString()) { hand = player; break; }
                binding.Arrow.textFormat.color = binding.ButtonColor;
                binding.Arrow.textFormat = binding.Arrow.textFormat;
                binding.Button.tooltips = (binding.Model.Open ? "收起手牌；再次展开会刷新" : "展开并刷新手牌") + "\n" + query.Status;
                binding.Strip.tooltips = query.Status + (query.IsStale ? " · 旧快照，等待更新" : "")
                    + "\n采样：" + (query.Snapshot?.sampledUtc ?? "—") + "\n手牌过多时滚轮横向查看";
                if (binding.Model.Open)
                {
                    string signature = HandStripModel.Signature(hand) + "/" + UnavailableReason;
                    if (signature != binding.Signature || binding.Height != height)
                    { Render(binding, hand, height); binding.Signature = signature; }
                    binding.Strip.SetSize(Math.Min(available, binding.ContentWidth), height);
                    binding.Offset = Math.Min(binding.Offset, Math.Max(0, binding.ContentWidth - binding.Strip.width));
                    binding.Content.x = -binding.Offset;
                }
            }
            Diagnose("已挂载升星右侧三角入口与四人独立手牌条");
        }
        void Render(Binding binding, PlayerHand hand, float height)
        {
            binding.Content.RemoveChildren(0, -1, true);
            binding.Height = height; binding.Offset = 0;
            var groups = HandStripModel.Group(hand);
            if (groups.Count == 0)
            {
                string text = hand == null ? (UnavailableReason ?? "等待手牌") : !hand.handKnown ? "手牌未知" : "空手牌（0）";
                float width = string.IsNullOrEmpty(UnavailableReason) ? 150 : 290;
                var bg = new GGraph(); bg.SetSize(width, height);
                bg.DrawRect(width, height, 1, Color.white, new Color(0.04f, 0.06f, 0.1f, 0.92f));
                bg.touchable = false; binding.Content.AddChild(bg);
                var placeholder = Text(text, width - 8, height, 18); placeholder.x = 4;
                binding.Content.AddChild(placeholder); binding.ContentWidth = width;
                binding.Content.SetSize(width, height); return;
            }
            float x = 0;
            foreach (var group in groups)
            {
                var data = group.Card;
                var cell = new GComponent();
                float width = height * 0.62f;
                try
                {
                    var config = StaticConfigure.Card.InfoDict[data.cardId];
                    var view = config.GetBattlePlayerCardView(binding.Model.PlayerId);
                    var card = UICom_Card.CreateInstance(); cell.AddChild(card);
                    var name = config.NameID.GetLocal(UIStringType.Card);
                    var description = config.DescId.GetLocal(UIStringType.Card);
                    var handData = new GameLogic.HandCardData(data.cardId, data.cardUid, data.purifyNum, data.isTemp, data.battleCost);
                    if (GameLogic.GameLogicManager.inst.card.cardActions.TryGetValue(data.cardId, out var action))
                        description = action.CardDescription(binding.Model.PlayerId, handData);
                    CommonUIManager.RendererCard(card, view, name, description, "", "", handData.BattleCost, config.CardType, config.CardTargetType);
                    if (card.height <= 0 || card.width <= 0) throw new InvalidOperationException("卡面尺寸为空");
                    float scale = height / card.height; width = card.width * scale;
                    card.SetScale(scale, scale); card.touchable = false;
                    cell.tooltips = name + " ×" + group.Count + "\n费用 " + handData.BattleCost
                        + " · 净化 " + data.purifyNum + (data.isTemp ? " · 临时牌" : "") + "\n" + description;
                }
                catch (Exception ex)
                {
                    string error = ex.GetType().Name + ": " + ex.Message;
                    if (binding.LastRenderError != error)
                    {
                        binding.LastRenderError = error;
                        SdkLog.Warn("HandViewer", "[card] 原生卡片失败 id=" + data.cardId + " " + error);
                    }
                    cell.RemoveChildren(0, -1, true);
                    try
                    {
                        // The small strip only needs artwork. Use the game's Addressables loader,
                        // without depending on full-card controllers or contextual skill descriptions.
                        var config = StaticConfigure.Card.InfoDict[data.cardId];
                        var image = new TextureLoader { touchable = false, fill = FillType.Scale, url = config.GetImage() };
                        image.SetSize(width, height); cell.AddChild(image);
                        cell.tooltips = config.NameID.GetLocal(UIStringType.Card) + " ×" + group.Count;
                    }
                    catch (Exception imageError)
                    {
                        SdkLog.Warn("HandViewer", "[card] 卡图失败 id=" + data.cardId + " " + imageError.GetType().Name + ": " + imageError.Message);
                        cell.AddChild(Text("卡图不可用", width, height, 14));
                    }
                }
                cell.SetSize(width, height); cell.SetXY(x, 0);
                if (group.Count > 1)
                {
                    float badgeWidth = group.Count >= 10 ? 32 : 24;
                    var bg = new GGraph(); bg.SetSize(badgeWidth, 25); bg.SetXY(Math.Max(0, width - badgeWidth), 0);
                    bg.DrawRect(badgeWidth, 25, 1, Color.white, new Color(0.05f, 0.07f, 0.12f, 0.95f)); bg.touchable = false;
                    cell.AddChild(bg);
                    var count = Text(group.Count.ToString(), badgeWidth, 25, 20); count.SetXY(bg.x + 3, 0); cell.AddChild(count);
                }
                binding.Content.AddChild(cell); x += width + 4;
            }
            binding.ContentWidth = Math.Max(1, x - 4); binding.Content.SetSize(binding.ContentWidth, height);
        }
        static UIBattleInfoPanel Find(GObject node, int depth)
        {
            if (node == null || depth > 12) return null;
            if (node is UIBattleInfoPanel panel) return panel;
            if (node is GComponent component)
                for (int i = 0; i < component.numChildren; i++) { var found = Find(component.GetChildAt(i), depth + 1); if (found != null) return found; }
            return null;
        }
        void ClearBindings()
        {
            foreach (var binding in bindings)
            {
                try { binding.Button.RemoveFromParent(); binding.Button.Dispose(); } catch { }
                try { binding.Strip.RemoveFromParent(); binding.Strip.Dispose(); } catch { }
            }
            bindings.Clear();
        }
        public void Dispose() { ClearBindings(); }
    }
}
