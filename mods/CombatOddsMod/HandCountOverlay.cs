using System;
using CesiumLoader.SDK.Logging;
using FairyGUI;
using GameLogic;
using Tools;
using UnityEngine;

namespace CombatOddsMod
{
    /// <summary>
    /// 左侧所有玩家的「剩余手牌」角标: 游戏自己的卡牌图标 + 当前手牌数, 排在技能 CD 文本右侧。
    ///
    /// 数据: <c>BattlePlayerData.cardContainer</c>。自己的手牌用本机列表长度(有些同步只更新
    /// 列表不回写计数), 队友用 <c>CardCount</c>(与游戏玩家信息面板同源, 掩码不影响数量)。
    /// 每 0.1 秒读一次实时值, 所以出牌/摸牌/换牌/净化造成的数量变化都会在下一帧反映出来。
    ///
    /// 位置: 与 <see cref="SkillCooldownOverlay"/> 的 CD 文本同一行、紧靠其右侧; CD 还没显示时
    /// 放在 CD 行的默认位置(昵称上方居中偏右), 两者都不会改动游戏自己的元素。
    ///
    /// 图标: 优先用游戏手牌状态文字里内联的图标(<c>txt_CardState</c> 的 [img]ui://...[/img]),
    /// 没有时退回游戏公共 UI 的卡牌图标 <c>Common/Com_Icon_Card</c>。取不到图标时只显示数字,
    /// 不影响读数。全部只读, 不改游戏任何元素。
    /// </summary>
    internal sealed class HandCountOverlay
    {
        private const float TickInterval = 0.1f;
        private const float CdGap = 6f;

        private sealed class Badge
        {
            public GObject Anchor;          // CD 行的锚点(昵称文本); 换人/重建时按引用判断
            public GComponent Box;
            public GGraph Bg;
            public GImage Icon;
            public GTextField Text;
            public long PlayerId;
            public int Shown = int.MinValue;
            public int FontSize;
            public float Width;
            public float Height;
        }

        private readonly Badge[] _badges = new Badge[4];
        private readonly SkillCooldownOverlay _cd;
        private UI.UIBattleInfoPanel _panel;
        private bool _enabled = true;
        private bool _started;
        private bool _warned;
        private bool _logDetail;

        private GObject _iconSource;       // 图标来源对象(保留引用, 卸载时释放)
        private NTexture _iconTexture;
        private bool _iconResolved;

        internal HandCountOverlay(SkillCooldownOverlay cd)
        {
            _cd = cd;
        }

        public void Start()
        {
            if (_started) return;
            Timers.inst.Add(TickInterval, 0, Refresh);
            _started = true;
        }

        /// <summary>开关只改标记: 配置热重载可能来自后台线程, 真正的隐藏发生在主线程刷新里。</summary>
        public void SetEnabled(bool enabled) { _enabled = enabled; }

        /// <summary>把每次数量变化写进加载器日志(排查显示问题用)。</summary>
        public void SetLogDetail(bool enabled) { _logDetail = enabled; }

        public void Dispose()
        {
            if (_started) Timers.inst.Remove(Refresh);
            _started = false;
            Reset();
        }

        private void Reset()
        {
            for (int i = 0; i < _badges.Length; i++) DisposeBadge(i);
            _panel = null;
            if (_iconSource != null)
            {
                try { _iconSource.Dispose(); } catch { }
                _iconSource = null;
            }
            _iconTexture = null;
            _iconResolved = false;
        }

        private void HideAll()
        {
            for (int i = 0; i < _badges.Length; i++)
                if (_badges[i] != null && _badges[i].Box != null && !_badges[i].Box.isDisposed)
                    _badges[i].Box.visible = false;
        }

        private void Refresh(object unused)
        {
            try
            {
                if (!_enabled) { HideAll(); return; }
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null || gm.battle == null)
                {
                    Reset();
                    return;
                }
                if (_panel == null || _panel.isDisposed || _panel.parent == null)
                {
                    Reset();
                    _panel = FindPanel(GRoot.inst, 0);
                }
                if (_panel == null) return;

                var container = _panel.com_BattlePlayer != null ? _panel.com_BattlePlayer.com_Container : null;
                if (container == null) return;
                bool hudVisible = _panel.visible && _panel.scaleX != 0f && _panel.scaleY != 0f
                    && _panel.com_BattlePlayer.visible
                    && _panel.com_BattlePlayer.scaleX != 0f && _panel.com_BattlePlayer.scaleY != 0f
                    && container.scaleX != 0f && container.scaleY != 0f;
                var slots = new[] { container.com_Player_1, container.com_Player_2,
                    container.com_Player_3, container.com_Player_4 };
                for (int i = 0; i < slots.Length; i++)
                {
                    try { RefreshSlot(i, slots[i], hudVisible); }
                    catch (Exception e)
                    {
                        HideSlot(i);
                        WarnOnce(e);
                    }
                }
            }
            catch (Exception e) { HideAll(); WarnOnce(e); }
        }

        private void RefreshSlot(int index, UI.UIBattleInfo_Button_PlayerInfo slot, bool hudVisible)
        {
            var player = slot != null ? slot.PlayerData : null;
            var info = slot != null ? slot.com_player as UI.UICom_PlayerInfo : null;
            var anchor = info != null ? info.txt_PlayerName : null;   // 和 CD 文本同一个锚点/父容器
            if (player == null || player.player == null || player.player.Id == 0 || player.cardContainer == null ||
                anchor == null || anchor.isDisposed || anchor.parent == null)
            {
                HideSlot(index);
                return;
            }

            int count = ReadCount(player);
            var badge = _badges[index];
            if (badge == null || badge.Box == null || badge.Box.isDisposed ||
                !ReferenceEquals(badge.Anchor, anchor) || badge.Box.parent != anchor.parent)
            {
                DisposeBadge(index);
                badge = CreateBadge(index, anchor, info, player.player.Id);
                if (badge == null) return;
            }

            if (badge.Shown != count)
            {
                badge.Shown = count;
                badge.Text.text = HandCountRules.Format(count);
                if (_logDetail)
                    SdkLog.Info("CombatOdds", "[手牌] slot=" + index + " player=" + player.player.Id
                        + " 剩余=" + count);
            }
            badge.Text.color = count <= 0
                ? new Color32(0xFF, 0x9A, 0x6A, 0xFF)   // 没牌: 暖色, 和正常读数区分
                : Color.white;

            Layout(badge, anchor, index);
            badge.Box.visible = hudVisible && slot.visible && anchor.visible;
        }

        private static int ReadCount(BattlePlayerData player)
        {
            int server = 0;
            try { server = player.cardContainer.CardCount; } catch { }
            bool isSelf = false;
            try { isSelf = SimpleSingletonProvider<GameLogicManager>.inst.account.IsSelf(player.player.Id); } catch { }
            int selfHand = -1;
            if (isSelf)
            {
                try
                {
                    var hand = player.cardContainer._HandCards;
                    if (hand != null) selfHand = hand.Count;
                }
                catch { }
            }
            return HandCountRules.Resolve(server, selfHand, isSelf);
        }

        private Badge CreateBadge(int index, GTextField anchor, UI.UICom_PlayerInfo info, long playerId)
        {
            try
            {
                var parent = anchor != null ? anchor.parent : null;
                if (parent == null) return null;
                ResolveIconTexture();

                var box = new GComponent { name = "cesium-hand-count", touchable = false, opaque = false };
                var bg = new GGraph { touchable = false };
                box.AddChild(bg);

                GImage icon = null;
                if (_iconTexture != null)
                {
                    icon = new GImage { texture = _iconTexture, touchable = false };
                    box.AddChild(icon);
                }

                var text = new GTextField { touchable = false, autoSize = AutoSizeType.Both };
                text.textFormat = CloneFormat(info);
                box.AddChild(text);

                parent.AddChild(box);
                var badge = new Badge
                {
                    Anchor = anchor,
                    Box = box,
                    Bg = bg,
                    Icon = icon,
                    Text = text,
                    PlayerId = playerId,
                    FontSize = 0,
                };
                _badges[index] = badge;
                return badge;
            }
            catch (Exception e)
            {
                WarnOnce(e);
                return null;
            }
        }

        /// <summary>
        /// 每 0.1 秒跟一次几何: CD 文本在它自己的父容器里定位(昵称上方居中), 角标就贴在它右侧;
        /// CD 还没显示时放到 CD 行的默认位置(昵称上方居中偏右), CD 出现后自然接上去。
        /// </summary>
        private void Layout(Badge badge, GTextField anchor, int index)
        {
            var cd = _cd != null ? _cd.LabelAt(index) : null;
            bool cdShown = cd != null && cd.visible && cd.width > 0.5f && cd.height > 0.5f;

            float anchorFont = anchor.textFormat != null ? anchor.textFormat.size : 20f;
            float badgeHeight = cdShown
                ? Mathf.Clamp(cd.height, 18f, 32f)
                : Mathf.Clamp(anchorFont * 1.25f, 18f, 32f);
            float pad = Mathf.Max(2f, badgeHeight * 0.16f);
            int fontSize = Mathf.Max(12, (int)Math.Round(badgeHeight * 0.78f));
            if (fontSize != badge.FontSize)
            {
                badge.FontSize = fontSize;
                var format = badge.Text.textFormat;
                if (format != null)
                {
                    format.size = fontSize;
                    badge.Text.textFormat = format;
                }
            }

            float iconHeight = badgeHeight - pad * 2f;
            float iconWidth = 0f;
            if (badge.Icon != null && _iconTexture != null && _iconTexture.height > 0)
            {
                float aspect = (float)_iconTexture.width / _iconTexture.height;
                iconWidth = iconHeight * aspect;
                float maxWidth = badgeHeight * 1.3f;      // 太宽的图标按宽度收一下, 保持比例不拉扁
                if (iconWidth > maxWidth)
                {
                    iconWidth = maxWidth;
                    iconHeight = iconWidth / aspect;
                }
            }
            float gap = iconWidth > 0f ? Mathf.Max(2f, badgeHeight * 0.14f) : 0f;

            float textWidth = badge.Text.width > 0f ? badge.Text.width : fontSize;
            float textHeight = badge.Text.height > 0f ? badge.Text.height : fontSize;
            float width = pad + iconWidth + gap + textWidth + pad;

            if (Math.Abs(width - badge.Width) > 0.01f || Math.Abs(badgeHeight - badge.Height) > 0.01f)
            {
                badge.Width = width;
                badge.Height = badgeHeight;
                badge.Box.SetSize(width, badgeHeight);
                badge.Bg.DrawRect(width, badgeHeight, 1,
                    new Color(1f, 1f, 1f, 0.28f), new Color(0.03f, 0.05f, 0.08f, 0.84f));
            }

            if (badge.Icon != null) badge.Icon.SetSize(iconWidth, iconHeight);
            badge.Icon?.SetXY(pad, (badgeHeight - iconHeight) * 0.5f);
            badge.Text.SetXY(pad + iconWidth + gap, (badgeHeight - textHeight) * 0.5f);

            // 与 CD 文本同一父容器、同一坐标系: 紧靠 CD 右侧并与之垂直居中。
            float x, y;
            if (cdShown)
            {
                x = cd.x + cd.width + CdGap;
                y = cd.y + (cd.height - badgeHeight) * 0.5f;
            }
            else
            {
                x = anchor.x + anchor.width * 0.5f + CdGap;
                y = anchor.y - badgeHeight + 5f;   // 与 CD 文本的默认行高一致
            }
            badge.Box.SetXY(x, y);
            if (badge.Box.parent != null)
                badge.Box.parent.SetChildIndex(badge.Box, badge.Box.parent.numChildren - 1);
        }

        private void HideSlot(int index)
        {
            var badge = _badges[index];
            if (badge != null && badge.Box != null && !badge.Box.isDisposed) badge.Box.visible = false;
        }

        private void DisposeBadge(int index)
        {
            var badge = _badges[index];
            _badges[index] = null;
            if (badge == null || badge.Box == null) return;
            try
            {
                if (badge.Box.parent != null) badge.Box.parent.RemoveChild(badge.Box, true);
                else if (!badge.Box.isDisposed) badge.Box.Dispose();
            }
            catch { }
        }

        private void WarnOnce(Exception e)
        {
            if (_warned) return;
            _warned = true;
            SdkLog.Warn("CombatOdds", "手牌计数显示失败: " + e.Message);
        }

        // ---------- 图标 ----------

        /// <summary>优先取游戏手牌文字里内联的图标, 取不到用公共 UI 的卡牌图标; 都没有就不画图标。</summary>
        private void ResolveIconTexture()
        {
            if (_iconResolved) return;
            _iconResolved = true;
            try
            {
                string url = ReadHandStateIconUrl();
                if (!string.IsNullOrEmpty(url)) _iconTexture = TextureFromUrl(url);
            }
            catch { }
            if (_iconTexture == null)
            {
                try { _iconTexture = TextureFromPackage("Common", "Com_Icon_Card"); } catch { }
            }
            if (_iconTexture == null)
                SdkLog.Warn("CombatOdds", "[手牌] 取不到游戏卡牌图标, 只显示数字");
        }

        /// <summary>游戏底部手牌状态文字(如「0/1」)如果内联了卡牌图标, 直接用它的地址。</summary>
        private static string ReadHandStateIconUrl()
        {
            try
            {
                var battle = SimpleSingletonProvider<GameLogicManager>.inst?.battle;
                var panel = battle != null && battle.handCard != null ? battle.handCard.UIObject : null;
                var text = panel != null ? panel.txt_CardState : null;
                if (text != null && !text.isDisposed)
                    return HandCountRules.ExtractIconUrl(text.text);
            }
            catch { }
            return null;
        }

        private NTexture TextureFromUrl(string url)
        {
            try
            {
                var obj = UIPackage.CreateObjectFromURL(url);
                if (obj == null) return null;
                var texture = FindTexture(obj, 0);
                if (texture == null) { obj.Dispose(); return null; }
                _iconSource = obj;      // 保留到卸载, 纹理由游戏包管理, 这里不释放
                return texture;
            }
            catch { return null; }
        }

        private NTexture TextureFromPackage(string package, string item)
        {
            try
            {
                var obj = UIPackage.CreateObject(package, item);
                if (obj == null) return null;
                var texture = FindTexture(obj, 0);
                if (texture == null) { obj.Dispose(); return null; }
                _iconSource = obj;
                return texture;
            }
            catch { return null; }
        }

        private static NTexture FindTexture(GObject obj, int depth)
        {
            if (obj == null || depth > 4) return null;
            if (obj is GImage image) return image.texture;
            if (obj is GLoader loader && loader.texture != null) return loader.texture;
            if (obj is GComponent component)
                for (int i = 0; i < component.numChildren; i++)
                {
                    var found = FindTexture(component.GetChildAt(i), depth + 1);
                    if (found != null) return found;
                }
            return null;
        }

        /// <summary>数字沿用昵称的字体(中文 TrueType), 加粗描边保证压在头像上也看得清。</summary>
        private static TextFormat CloneFormat(UI.UICom_PlayerInfo info)
        {
            TextFormat src = null;
            try { src = info != null && info.txt_PlayerName != null ? info.txt_PlayerName.textFormat : null; } catch { }
            var format = new TextFormat();
            if (src != null)
            {
                format.font = src.font;
                format.size = src.size;
                format.bold = src.bold;
                format.italic = src.italic;
                format.underline = src.underline;
                format.strikethrough = src.strikethrough;
                format.align = src.align;
                format.lineSpacing = src.lineSpacing;
                format.letterSpacing = src.letterSpacing;
                format.outline = src.outline;
                format.outlineColor = src.outlineColor;
                format.outlineSoftness = src.outlineSoftness;
                format.shadowColor = src.shadowColor;
                format.shadowOffset = src.shadowOffset;
                format.gradientColor = src.gradientColor;
                format.faceDilate = src.faceDilate;
                format.underlaySoftness = src.underlaySoftness;
                format.specialStyle = src.specialStyle;
            }
            format.bold = true;
            format.outline = 2f;
            format.outlineColor = Color.black;
            return format;
        }

        private static UI.UIBattleInfoPanel FindPanel(GObject node, int depth)
        {
            if (node == null || depth > 12) return null;
            if (node is UI.UIBattleInfoPanel panel) return panel;
            if (node is GComponent component)
                for (int i = 0; i < component.numChildren; i++)
                {
                    var found = FindPanel(component.GetChildAt(i), depth + 1);
                    if (found != null) return found;
                }
            return null;
        }
    }
}
