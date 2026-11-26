using System;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;
using FairyGUI;
using GameLogic;
using Tools;
using UI;
using UnityEngine;

namespace ToysBadgeMod
{
    internal sealed class BadgeOverlay : IDisposable
    {
        private const float TickIntervalSeconds = 0.15f;
        private const float HoverPollIntervalSeconds = 0.05f;
        private const float PanelSearchIntervalSeconds = 0.5f;
        private const int MaxWalkDepth = 12;

        private const string ModLabIconAssetKey = "UT_Item_PlayerPhoto_70120";
        private static readonly Color ModLabRimColor = new Color(248f / 255f, 250f / 255f, 1f, 0.88f);
        private static readonly Color ModLabBgColor = Color.black;

        private const int TipSortingOrder = 30000;
        private const int TipFontSize = 18;
        private const float TipPadX = 10f;
        private const float TipPadY = 7f;
        private const float TipGap = 8f;

        private static readonly Color TipBgColor = new Color(0.05f, 0.06f, 0.09f, 0.95f);
        private static readonly Color TipBorderColor = new Color(1f, 1f, 1f, 0.28f);

        private sealed class SlotBadge
        {
            public UIBattleInfo_Button_PlayerInfo Slot;
            public UICom_PlayerInfo Info;
            public GComponent Box;
            public GImage Icon;
            public GComponent ModLabGroup;
            public GGraph ModLabRim;
            public GGraph ModLabBg;
            public GComponent ModLabClip;
            public GGraph ModLabMask;
            public UI.TextureLoader ModLabLoader;
            public int ModLabLayoutSize;
            public long PlayerId;
            public BadgeClientKind ClientKind;
            public int IconSize;
            public float LastX;
            public float LastY;
        }

        private readonly SlotBadge[] _slots = new SlotBadge[4];
        private UIBattleInfoPanel _panel;
        private float _nextTickAt;
        private float _nextHoverAt;
        private float _nextPanelSearchAt;

        private bool _hasRestHeadRect;
        private float _restHeadX;
        private float _restHeadY;
        private float _restHeadH;

        private Texture2D _nativeTexture;
        private NTexture _nTexture;
        private string _loadedIconPath;
        private bool _textureLoaded;
        private bool _warned;

        private GComponent _tipBox;
        private GGraph _tipBorder;
        private GGraph _tipBg;
        private GTextField _tipText;

        private static bool _mouseProbed;
        private static PropertyInfo _mousePosProp;
        private static Func<Vector3> _mouseGetter;

        public void Tick(float now, ToysBadgeConfig cfg, PresenceSession session)
        {
            if (cfg == null || !cfg.Enabled || session == null || !session.HasActiveMatch)
            {
                HideAll();
                HideTip();
                return;
            }

            if (now >= _nextTickAt)
            {
                _nextTickAt = now + TickIntervalSeconds;
                RefreshLayout(now, cfg, session);
            }

            if (now >= _nextHoverAt)
            {
                _nextHoverAt = now + HoverPollIntervalSeconds;
                UpdateHoverTooltip();
            }
        }

        public void InvalidateTexture()
        {
            _textureLoaded = false;
            _loadedIconPath = null;
        }

        public void Dispose()
        {
            HideTip();
            DisposeTipBox();
            ResetPanelAndBadges();
            DisposeTexture();
        }

        private void RefreshLayout(float now, ToysBadgeConfig cfg, PresenceSession session)
        {
            try
            {
                if (!SimpleSingletonProvider<GameLogicManager>.hasInstance)
                {
                    ResetPanelAndBadges();
                    return;
                }

                GameLogicManager gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null || gm.battle == null)
                {
                    ResetPanelAndBadges();
                    return;
                }

                if (_panel == null || _panel.isDisposed || _panel.parent == null)
                {
                    _panel = FindPanel(now);
                }
                if (_panel == null)
                {
                    HideAll();
                    HideTip();
                    return;
                }

                UIBattleInfo_Com_Player battlePlayer = _panel.com_BattlePlayer;
                UIBattleInfo_Com_PlayerContainer container = battlePlayer != null ? battlePlayer.com_Container : null;
                if (container == null)
                {
                    HideAll();
                    HideTip();
                    return;
                }

                bool hudVisible = _panel.visible
                    && _panel.scaleX != 0f
                    && _panel.scaleY != 0f
                    && battlePlayer.visible
                    && battlePlayer.scaleX != 0f
                    && battlePlayer.scaleY != 0f
                    && container.visible
                    && container.scaleX != 0f
                    && container.scaleY != 0f;

                if (!hudVisible)
                {
                    HideAll();
                    HideTip();
                    return;
                }

                UIBattleInfo_Button_PlayerInfo[] slotViews = new UIBattleInfo_Button_PlayerInfo[]
                {
                    container.com_Player_1,
                    container.com_Player_2,
                    container.com_Player_3,
                    container.com_Player_4
                };

                UpdateRestingHeadRect(slotViews);

                for (int i = 0; i < slotViews.Length; i++)
                {
                    try
                    {
                        RefreshSlot(i, slotViews[i], gm, cfg, session);
                    }
                    catch (Exception ex)
                    {
                        HideSlot(i);
                        WarnOnce(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                HideAll();
                HideTip();
                WarnOnce(ex);
            }
        }

        private void UpdateRestingHeadRect(UIBattleInfo_Button_PlayerInfo[] slotViews)
        {
            UIBattleInfo_Button_PlayerInfo visibleRestSlot = null;
            UICom_PlayerInfo visibleRestInfo = null;
            GObject visibleRestHead = null;

            UIBattleInfo_Button_PlayerInfo anyRestSlot = null;
            UICom_PlayerInfo anyRestInfo = null;
            GObject anyRestHead = null;

            for (int i = 0; i < slotViews.Length; i++)
            {
                UIBattleInfo_Button_PlayerInfo slot = slotViews[i];
                if (slot == null || slot.isDisposed) continue;
                UICom_PlayerInfo info = slot.com_player as UICom_PlayerInfo;
                if (info == null || info.isDisposed) continue;
                GObject head = info.com_Head;
                if (head == null || head.isDisposed || head.width <= 1f || head.height <= 1f) continue;

                bool isPlaying = info.actionShow != null && info.actionShow.playing;
                if (isPlaying) continue;
                if (Math.Abs(head.rotation) >= 0.01f || Math.Abs(info.rotation) >= 0.01f) continue;

                if (slot.visible && info.visible && head.visible)
                {
                    visibleRestSlot = slot;
                    visibleRestInfo = info;
                    visibleRestHead = head;
                    break;
                }

                if (anyRestSlot == null)
                {
                    anyRestSlot = slot;
                    anyRestInfo = info;
                    anyRestHead = head;
                }
            }

            UIBattleInfo_Button_PlayerInfo chosenSlot = visibleRestSlot != null ? visibleRestSlot : anyRestSlot;
            UICom_PlayerInfo chosenInfo = visibleRestInfo != null ? visibleRestInfo : anyRestInfo;
            GObject chosenHead = visibleRestHead != null ? visibleRestHead : anyRestHead;
            if (chosenSlot == null || chosenInfo == null || chosenHead == null) return;

            Vector2 tl = chosenSlot.GlobalToLocal(chosenHead.LocalToGlobal(Vector2.zero));
            Vector2 br = chosenSlot.GlobalToLocal(chosenHead.LocalToGlobal(new Vector2(chosenHead.width, chosenHead.height)));
            float h = br.y - tl.y;
            if (h <= 0f) h = chosenHead.height;

            _restHeadX = tl.x;
            _restHeadY = tl.y;
            _restHeadH = h;
            _hasRestHeadRect = true;
        }

        private void RefreshSlot(
            int index,
            UIBattleInfo_Button_PlayerInfo slot,
            GameLogicManager gm,
            ToysBadgeConfig cfg,
            PresenceSession session)
        {
            if (slot == null || slot.isDisposed || !slot.visible)
            {
                HideSlot(index);
                return;
            }

            BattlePlayerData pd = slot.PlayerData;
            if (pd == null || pd.player == null || pd.player.Id == 0)
            {
                HideSlot(index);
                return;
            }

            UICom_PlayerInfo info = slot.com_player as UICom_PlayerInfo;
            if (info == null || info.isDisposed || !info.visible || info.parent == null)
            {
                HideSlot(index);
                return;
            }

            GObject head = info.com_Head;
            if (head == null || head.isDisposed || !head.visible)
            {
                HideSlot(index);
                return;
            }

            long playerId = pd.player.Id;
            bool isSelf = false;
            try
            {
                if (gm.account != null) isSelf = gm.account.IsSelf(playerId);
            }
            catch { isSelf = false; }

            string rawNick = "";
            try { rawNick = pd.player.GetNick(); }
            catch { rawNick = ""; }

            BadgeClientKind clientKind = session.GetPlayerBadgeKind(playerId, rawNick, isSelf, cfg.ShowSelfBadge, cfg.ShowOfflineMembers);
            if (clientKind == BadgeClientKind.None)
            {
                HideSlot(index);
                return;
            }

            NTexture tex = clientKind == BadgeClientKind.ModLab ? null : EnsureTexture(cfg.IconPath);
            if (clientKind != BadgeClientKind.ModLab && tex == null)
            {
                HideSlot(index);
                return;
            }

            SlotBadge badge = _slots[index];
            if (badge == null
                || badge.Box == null
                || badge.Box.isDisposed
                || !object.ReferenceEquals(badge.Slot, slot)
                || badge.Box.parent != slot
                || badge.PlayerId != playerId)
            {
                DisposeSlot(index);
                badge = CreateSlotBadge(index, slot, info, playerId, tex);
                if (badge == null) return;
            }
            else
            {
                badge.Info = info;
                if (tex != null && badge.Icon != null && badge.Icon.texture != tex)
                {
                    badge.Icon.texture = tex;
                }
            }
            badge.ClientKind = clientKind;

            int size = cfg.IconSize;
            if (badge.IconSize != size)
            {
                badge.IconSize = size;
                badge.Box.SetSize(size, size);
                if (badge.Icon != null)
                {
                    badge.Icon.SetSize(size, size);
                    badge.Icon.SetXY(0f, 0f);
                }
            }

            if (clientKind == BadgeClientKind.ModLab)
            {
                EnsureModLabVisual(badge);
                if (badge.ModLabLayoutSize != size)
                {
                    LayoutModLabVisual(badge, size);
                }
                if (badge.Icon != null) badge.Icon.visible = false;
                if (badge.ModLabGroup != null) badge.ModLabGroup.visible = true;
            }
            else
            {
                if (badge.Icon != null) badge.Icon.visible = true;
                if (badge.ModLabGroup != null) badge.ModLabGroup.visible = false;
            }

            float restX;
            float restY;
            float restH;
            if (_hasRestHeadRect)
            {
                restX = _restHeadX;
                restY = _restHeadY;
                restH = _restHeadH;
            }
            else
            {
                float infoX = info.pivotAsAnchor ? info.x - info.pivotX * info.width : info.x;
                float infoY = info.pivotAsAnchor ? info.y - info.pivotY * info.height : info.y;
                float headX = head.pivotAsAnchor ? head.x - head.pivotX * head.width : head.x;
                float headY = head.pivotAsAnchor ? head.y - head.pivotY * head.height : head.y;
                restX = infoX + headX;
                restY = infoY + headY;
                restH = head.height;
            }

            float localX = restX - size - cfg.Gap + cfg.OffsetX;
            float localY = restY + (restH - size) * 0.5f + cfg.OffsetY;

            Vector2 desiredGlobal = slot.LocalToGlobal(new Vector2(localX, localY));
            float minGlobalX = 4f;
            try
            {
                if (GRoot.inst != null)
                {
                    minGlobalX = GRoot.inst.LocalToGlobal(new Vector2(4f, 0f)).x;
                }
            }
            catch { minGlobalX = 4f; }

            if (desiredGlobal.x < minGlobalX)
            {
                desiredGlobal.x = minGlobalX;
                Vector2 clampedLocal = slot.GlobalToLocal(desiredGlobal);
                localX = clampedLocal.x;
            }

            if (Math.Abs(localX - badge.LastX) > 0.01f || Math.Abs(localY - badge.LastY) > 0.01f)
            {
                badge.LastX = localX;
                badge.LastY = localY;
                badge.Box.SetXY(localX, localY);
            }

            if (badge.Box.parent != null)
            {
                int topIndex = badge.Box.parent.numChildren - 1;
                if (topIndex >= 0 && badge.Box.parent.GetChildIndex(badge.Box) != topIndex)
                {
                    badge.Box.parent.SetChildIndex(badge.Box, topIndex);
                }
            }
            badge.Box.visible = true;
        }

        private SlotBadge CreateSlotBadge(
            int index,
            UIBattleInfo_Button_PlayerInfo slot,
            UICom_PlayerInfo info,
            long playerId,
            NTexture tex)
        {
            GComponent box = new GComponent();
            box.name = "ToysBadgeMod.Badge";
            box.touchable = false;
            box.opaque = false;
            box.sortingOrder = 120;

            GImage icon = new GImage();
            icon.name = "ToysBadgeMod.Icon";
            if (tex != null) icon.texture = tex;
            icon.touchable = false;
            box.AddChild(icon);

            slot.AddChild(box);

            SlotBadge badge = new SlotBadge();
            badge.Slot = slot;
            badge.Info = info;
            badge.Box = box;
            badge.Icon = icon;
            badge.PlayerId = playerId;
            badge.IconSize = 0;
            badge.LastX = float.NaN;
            badge.LastY = float.NaN;
            _slots[index] = badge;
            return badge;
        }

        private static void EnsureModLabVisual(SlotBadge badge)
        {
            if (badge == null || badge.Box == null || badge.Box.isDisposed) return;
            if (badge.ModLabGroup != null && !badge.ModLabGroup.isDisposed) return;

            GComponent group = new GComponent();
            group.name = "ToysBadgeMod.ModLabGroup";
            group.touchable = false;
            group.opaque = false;

            GGraph rim = new GGraph();
            rim.name = "ToysBadgeMod.ModLabRim";
            rim.touchable = false;

            GGraph bg = new GGraph();
            bg.name = "ToysBadgeMod.ModLabBg";
            bg.touchable = false;

            GComponent clip = new GComponent();
            clip.name = "ToysBadgeMod.ModLabClip";
            clip.touchable = false;
            clip.opaque = false;

            UI.TextureLoader loader = new UI.TextureLoader();
            loader.name = "ToysBadgeMod.ModLabIcon";
            loader.touchable = false;
            loader.autoSize = false;
            loader.fill = FillType.Scale;
            loader.align = AlignType.Center;
            loader.verticalAlign = VertAlignType.Middle;
            loader.showErrorSign = false;

            GGraph mask = new GGraph();
            mask.name = "ToysBadgeMod.ModLabMask";
            mask.touchable = false;

            clip.AddChild(loader);
            clip.AddChild(mask);
            clip.mask = mask.displayObject;

            group.AddChild(rim);
            group.AddChild(bg);
            group.AddChild(clip);
            badge.Box.AddChild(group);

            badge.ModLabGroup = group;
            badge.ModLabRim = rim;
            badge.ModLabBg = bg;
            badge.ModLabClip = clip;
            badge.ModLabMask = mask;
            badge.ModLabLoader = loader;
            badge.ModLabLayoutSize = 0;

            loader.url = ModLabIconAssetKey;
        }

        private static void LayoutModLabVisual(SlotBadge badge, int size)
        {
            if (badge == null || badge.ModLabGroup == null || badge.ModLabGroup.isDisposed) return;

            float s = size;
            float outerMargin = s * (1.5f / 96f);
            float outerDiam = s - outerMargin * 2f;
            if (outerDiam < 2f) outerDiam = 2f;
            float ring = s * (1.5f / 96f);
            if (ring < 0.8f) ring = 0.8f;
            float innerDiam = outerDiam - ring * 2f;
            if (innerDiam < 1f) innerDiam = 1f;
            float innerOffset = (s - innerDiam) * 0.5f;

            badge.ModLabGroup.SetSize(s, s);
            badge.ModLabGroup.SetXY(0f, 0f);

            badge.ModLabRim.SetXY(outerMargin, outerMargin);
            badge.ModLabRim.SetSize(outerDiam, outerDiam);
            badge.ModLabRim.DrawEllipse(outerDiam, outerDiam, ModLabRimColor);

            badge.ModLabBg.SetXY(innerOffset, innerOffset);
            badge.ModLabBg.SetSize(innerDiam, innerDiam);
            badge.ModLabBg.DrawEllipse(innerDiam, innerDiam, ModLabBgColor);

            badge.ModLabClip.SetXY(innerOffset, innerOffset);
            badge.ModLabClip.SetSize(innerDiam, innerDiam);

            badge.ModLabLoader.SetXY(0f, 0f);
            badge.ModLabLoader.SetSize(innerDiam, innerDiam);

            badge.ModLabMask.SetXY(0f, 0f);
            badge.ModLabMask.SetSize(innerDiam, innerDiam);
            badge.ModLabMask.DrawEllipse(innerDiam, innerDiam, Color.white);
            badge.ModLabClip.mask = badge.ModLabMask.displayObject;

            badge.ModLabLayoutSize = size;
        }

        private void UpdateHoverTooltip()
        {
            try
            {
                Vector2 pointer;
                if (!TryReadStagePointer(out pointer))
                {
                    HideTip();
                    return;
                }

                SlotBadge hit = null;
                for (int i = 0; i < _slots.Length; i++)
                {
                    SlotBadge b = _slots[i];
                    if (b == null || b.Box == null || b.Box.isDisposed || !b.Box.visible) continue;
                    if (b.Box.width <= 0f || b.Box.height <= 0f) continue;

                    Vector2 localPt = b.Box.GlobalToLocal(pointer);
                    if (localPt.x >= -2f
                        && localPt.x <= b.Box.width + 2f
                        && localPt.y >= -2f
                        && localPt.y <= b.Box.height + 2f)
                    {
                        hit = b;
                        break;
                    }
                }

                if (hit != null)
                {
                    ShowTip(hit.Box, hit.Info, hit.ClientKind);
                }
                else
                {
                    HideTip();
                }
            }
            catch
            {
                HideTip();
            }
        }

        private static bool TryReadStagePointer(out Vector2 pointer)
        {
            pointer = Vector2.zero;
            try
            {
                if (!_mouseProbed)
                {
                    _mouseProbed = true;
                    Type inputType = RuntimeAssemblyService.FindType("UnityEngine.Input")
                        ?? RuntimeAssemblyService.FindTypeBySimpleName("Input");
                    if (inputType != null)
                    {
                        _mousePosProp = inputType.GetProperty(
                            "mousePosition",
                            BindingFlags.Public | BindingFlags.Static);
                        if (_mousePosProp != null)
                        {
                            try
                            {
                                MethodInfo getter = _mousePosProp.GetGetMethod();
                                if (getter != null)
                                {
                                    _mouseGetter = (Func<Vector3>)Delegate.CreateDelegate(typeof(Func<Vector3>), getter);
                                }
                            }
                            catch { _mouseGetter = null; }
                        }
                    }
                }

                if (_mouseGetter != null)
                {
                    Vector3 v = _mouseGetter();
                    pointer = new Vector2(v.x, Screen.height - v.y);
                    return true;
                }

                if (_mousePosProp != null)
                {
                    object raw = _mousePosProp.GetValue(null, null);
                    if (raw is Vector3)
                    {
                        Vector3 v = (Vector3)raw;
                        pointer = new Vector2(v.x, Screen.height - v.y);
                        return true;
                    }
                }
            }
            catch { }

            try
            {
                if (Stage.inst != null)
                {
                    pointer = Stage.inst.touchPosition;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private void ShowTip(GComponent owner, UICom_PlayerInfo sampleInfo, BadgeClientKind clientKind)
        {
            if (owner == null || owner.isDisposed) return;
            GRoot root = GRoot.inst;
            if (root == null) return;

            EnsureTipBox(root, sampleInfo);
            if (_tipBox == null || _tipBox.isDisposed || _tipText == null || _tipBorder == null || _tipBg == null) return;

            _tipText.text = PresenceProtocol.GetTooltipText(clientKind);

            float w = _tipText.textWidth + TipPadX * 2f;
            float h = _tipText.textHeight + TipPadY * 2f;
            if (w < 60f) w = 60f;
            if (h < 28f) h = 28f;

            float[] outerRadius = new float[] { 8f, 8f, 8f, 8f };
            float[] innerRadius = new float[] { 7f, 7f, 7f, 7f };
            _tipBorder.DrawRoundRect(w, h, TipBorderColor, outerRadius);
            _tipBg.SetXY(1f, 1f);
            _tipBg.DrawRoundRect(w - 2f, h - 2f, TipBgColor, innerRadius);
            _tipText.SetXY(TipPadX, TipPadY);
            _tipBox.SetSize(w, h);

            Vector2 ownerTL = root.GlobalToLocal(owner.LocalToGlobal(Vector2.zero));
            Vector2 ownerBR = root.GlobalToLocal(owner.LocalToGlobal(new Vector2(owner.width, owner.height)));

            float x = ownerBR.x + TipGap;
            float y = (ownerTL.y + ownerBR.y - h) * 0.5f;

            float maxX = root.width - w - 4f;
            float maxY = root.height - h - 4f;
            if (maxX < 4f) maxX = 4f;
            if (maxY < 4f) maxY = 4f;

            if (x > maxX) x = ownerTL.x - w - TipGap;
            if (x < 4f) x = 4f;
            if (x > maxX) x = maxX;

            if (y < 4f) y = 4f;
            if (y > maxY) y = maxY;

            _tipBox.SetXY(x, y);
            _tipBox.sortingOrder = TipSortingOrder;
            int topIndex = root.numChildren - 1;
            if (topIndex >= 0 && root.GetChildIndex(_tipBox) != topIndex)
            {
                root.SetChildIndex(_tipBox, topIndex);
            }
            _tipBox.visible = true;
        }

        private void EnsureTipBox(GRoot root, UICom_PlayerInfo sampleInfo)
        {
            if (_tipBox != null && !_tipBox.isDisposed && _tipBox.parent == root) return;

            DisposeTipBox();

            _tipBox = new GComponent();
            _tipBox.name = "ToysBadgeMod.Tooltip";
            _tipBox.touchable = false;
            _tipBox.opaque = false;
            _tipBox.sortingOrder = TipSortingOrder;

            _tipBorder = new GGraph();
            _tipBorder.touchable = false;
            _tipBox.AddChild(_tipBorder);

            _tipBg = new GGraph();
            _tipBg.touchable = false;
            _tipBox.AddChild(_tipBg);

            _tipText = new GTextField();
            _tipText.touchable = false;
            _tipText.autoSize = AutoSizeType.Both;
            _tipText.textFormat = BuildTooltipFormat(sampleInfo);
            _tipBox.AddChild(_tipText);

            root.AddChild(_tipBox);
            _tipBox.visible = false;
        }

        private TextFormat BuildTooltipFormat(UICom_PlayerInfo sampleInfo)
        {
            TextFormat tf = new TextFormat();
            TextFormat src = ResolveChineseTextFormat(sampleInfo);
            if (src != null)
            {
                tf.CopyFrom(src);
            }
            if (string.IsNullOrEmpty(tf.font))
            {
                try { tf.font = UIConfig.defaultFont; }
                catch { }
            }
            tf.size = TipFontSize;
            tf.color = Color.white;
            tf.outline = 0f;
            return tf;
        }

        private TextFormat ResolveChineseTextFormat(UICom_PlayerInfo sampleInfo)
        {
            try
            {
                if (_panel != null && _panel.txt_BuffTitle != null && _panel.txt_BuffTitle.textFormat != null)
                    return _panel.txt_BuffTitle.textFormat;
            }
            catch { }

            try
            {
                if (_panel != null && _panel.txt_Buff != null && _panel.txt_Buff.textFormat != null)
                    return _panel.txt_Buff.textFormat;
            }
            catch { }

            try
            {
                if (sampleInfo != null && sampleInfo.txt_PlayerName != null && sampleInfo.txt_PlayerName.textFormat != null)
                    return sampleInfo.txt_PlayerName.textFormat;
            }
            catch { }

            return null;
        }

        private void HideTip()
        {
            try
            {
                if (_tipBox != null && !_tipBox.isDisposed)
                {
                    _tipBox.visible = false;
                }
            }
            catch { }
        }

        private void DisposeTipBox()
        {
            try
            {
                if (_tipBox != null)
                {
                    if (_tipBox.parent != null)
                    {
                        _tipBox.parent.RemoveChild(_tipBox, true);
                    }
                    else if (!_tipBox.isDisposed)
                    {
                        _tipBox.Dispose();
                    }
                }
            }
            catch { }
            _tipBox = null;
            _tipBorder = null;
            _tipBg = null;
            _tipText = null;
        }

        private NTexture EnsureTexture(string iconPath)
        {
            string normPath = iconPath ?? "";
            if (_textureLoaded
                && string.Equals(_loadedIconPath, normPath, StringComparison.Ordinal)
                && _nTexture != null
                && !_nTexture.disposed
                && _nativeTexture != null)
            {
                return _nTexture;
            }

            DisposeTexture();
            _loadedIconPath = normPath;
            _textureLoaded = true;

            try
            {
                _nativeTexture = ToysIconData.LoadCustomTexture(normPath);
                if (_nativeTexture == null) return null;
                _nTexture = new NTexture(_nativeTexture);
                _nTexture.destroyMethod = DestroyMethod.None;
                return _nTexture;
            }
            catch (Exception ex)
            {
                WarnOnce(ex);
                DisposeTexture();
                return null;
            }
        }

        private void DisposeTexture()
        {
            try
            {
                if (_nTexture != null && !_nTexture.disposed)
                {
                    _nTexture.destroyMethod = DestroyMethod.None;
                    _nTexture.Dispose();
                }
            }
            catch { }
            _nTexture = null;

            try
            {
                if (_nativeTexture != null)
                {
                    UnityEngine.Object.Destroy(_nativeTexture);
                }
            }
            catch { }
            _nativeTexture = null;
        }

        private UIBattleInfoPanel FindPanel(float now)
        {
            if (now < _nextPanelSearchAt) return null;
            _nextPanelSearchAt = now + PanelSearchIntervalSeconds;
            if (GRoot.inst == null) return null;
            return WalkForPanel(GRoot.inst, 0);
        }

        private static UIBattleInfoPanel WalkForPanel(GObject node, int depth)
        {
            if (node == null || depth > MaxWalkDepth) return null;
            UIBattleInfoPanel panel = node as UIBattleInfoPanel;
            if (panel != null) return panel;

            GComponent comp = node as GComponent;
            if (comp != null)
            {
                int count = comp.numChildren;
                for (int i = 0; i < count; i++)
                {
                    UIBattleInfoPanel found = WalkForPanel(comp.GetChildAt(i), depth + 1);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void HideAll()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                HideSlot(i);
            }
        }

        private void HideSlot(int index)
        {
            SlotBadge badge = _slots[index];
            if (badge != null && badge.Box != null && !badge.Box.isDisposed)
            {
                badge.Box.visible = false;
            }
        }

        private void DisposeSlot(int index)
        {
            SlotBadge badge = _slots[index];
            _slots[index] = null;
            if (badge == null || badge.Box == null) return;
            try
            {
                if (badge.ModLabClip != null && !badge.ModLabClip.isDisposed)
                {
                    badge.ModLabClip.mask = null;
                }
                if (badge.Box.parent != null)
                {
                    badge.Box.parent.RemoveChild(badge.Box, true);
                }
                else if (!badge.Box.isDisposed)
                {
                    badge.Box.Dispose();
                }
            }
            catch { }
        }

        private void ResetPanelAndBadges()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                DisposeSlot(i);
            }
            _panel = null;
            _hasRestHeadRect = false;
            _restHeadX = 0f;
            _restHeadY = 0f;
            _restHeadH = 0f;
        }

        private void WarnOnce(Exception ex)
        {
            if (_warned) return;
            _warned = true;
            SdkLog.Warn("ToysBadgeMod", "图标叠加层渲染异常: " + (ex != null ? ex.Message : "unknown"));
        }
    }
}
