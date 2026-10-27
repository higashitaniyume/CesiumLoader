using System;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using FairyGUI;
using GameLogic;
using Tools;
using UnityEngine;

namespace CombatOddsMod
{
    /// <summary>
    /// 梅加斯（HeroId 122）的【轨道轰炸】理论伤害，显示在右下角发动技能按钮的正上方。
    ///
    /// 计算口径（与配置/文档一致）：
    ///   开炮次数 = 手牌张数 ÷ 2（向下取整）
    ///   单炮伤害 = 3 + 战斗牌总费用 ÷ 3（向上取整）
    ///   理论伤害 = 开炮次数 × 单炮伤害
    /// 例：8 张、总费用 14 → 4 炮 × 8 点 = 32 点。
    ///
    /// 锚点是技能按钮 <c>UIHandCard_Com_Skill</c>，不是「移动」按钮：回放会隐藏移动按钮，
    /// 但技能按钮仍在，因此回放里也能看到。手牌直接读 <c>GetSelfPlayerData()</c>（回放中
    /// 是当前观看的玩家）的实时手牌列表，不依赖“自己账号”判断。
    /// 只读取手牌与费用，不改游戏元素、不发任何指令。    /// </summary>
    internal sealed class MegasBombardmentOverlay
    {
        private const int MegasHeroId = 122;
        private const float TickInterval = 0.1f;
        private const float AnchorGap = 4f;
        private const float EdgePad = 4f;

        private static readonly Color32 TotalColor = new Color32(0xFF, 0xD2, 0x4A, 0xFF);
        private static readonly Color32 CardsColor = new Color32(0x7F, 0xD4, 0xFF, 0xFF);
        private static readonly Color32 CostColor = new Color32(0xC9, 0xA7, 0xFF, 0xFF);
        private static readonly Color32 ShotsColor = new Color32(0x5A, 0xE3, 0x7A, 0xFF);
        private static readonly Color32 PerShotColor = new Color32(0xFF, 0x9A, 0x5A, 0xFF);
        private static readonly Color32 LabelColor = new Color32(0xA9, 0xB4, 0xC3, 0xFF);

        private UI.UIHandCardPanel _panel;
        private GComponent _anchor;
        private GComponent _box;
        private GGraph _bg;
        private GTextField[] _titleParts;
        private GTextField[] _detailParts;
        private GTextField _zero;
        private bool _enabled = true;
        private bool _started;
        private bool _warned;
        private float _lastWidth = -1f;
        private float _lastHeight = -1f;
        private int _lastTitleSize;
        private string _shownKey;

        public void Start()
        {
            if (_started) return;
            Timers.inst.Add(TickInterval, 0, Refresh);
            _started = true;
        }

        /// <summary>开关只改标记：配置热重载可能来自后台线程，真正的隐藏发生在主线程刷新里。</summary>
        public void SetEnabled(bool enabled) { _enabled = enabled; }

        public void Dispose()
        {
            if (_started) Timers.inst.Remove(Refresh);
            _started = false;
            Reset();
        }

        private void Reset()
        {
            if (_box != null)
            {
                try
                {
                    if (_box.parent != null) _box.parent.RemoveChild(_box, true);
                    else if (!_box.isDisposed) _box.Dispose();
                }
                catch { }
            }
            _panel = null;
            _anchor = null;
            _box = null;
            _bg = null;
            _titleParts = null;
            _detailParts = null;
            _zero = null;
            _lastWidth = -1f;
            _lastHeight = -1f;
            _lastTitleSize = 0;
            _shownKey = null;
        }

        private void Hide()
        {
            if (_box != null && !_box.isDisposed) _box.visible = false;
        }

        private void Refresh(object unused)
        {
            try
            {
                if (!_enabled) { Hide(); return; }

                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null || gm.battle == null) { Reset(); return; }

                var self = gm.battle.GetSelfPlayerData();
                if (self?.player?.Hero == null || self.player.Hero.HeroId != MegasHeroId)
                {
                    // 不是梅加斯：保留已建好的控件但隐藏，换角色/重开时能直接复用。
                    Hide();
                    return;
                }

                var panel = FindPanel(gm);
                if (panel == null || panel.com_Skill == null || panel.com_Skill.isDisposed)
                {
                    Reset();
                    return;
                }

                if (!ReferenceEquals(panel, _panel) || !ReferenceEquals(panel.com_Skill, _anchor) ||
                    _box == null || _box.isDisposed || _box.parent != panel)
                {
                    Reset();
                    _panel = panel;
                    _anchor = panel.com_Skill;
                    if (!Build(panel)) return;
                }

                int cards, totalCost;
                ReadHand(self, out cards, out totalCost);
                int shots = CombatMath.MegasOrbitalBombardmentCount(cards);
                int perShot = CombatMath.MegasOrbitalBombardmentDamage(cards, totalCost);
                int total = CombatMath.MegasOrbitalBombardmentTotalDamage(cards, totalCost);

                UpdateText(cards, totalCost, shots, perShot, total);
                Layout(panel);
                _box.visible = panel.visible && _anchor.visible;
            }
            catch (Exception e)
            {
                Hide();
                WarnOnce(e);
            }
        }

        private static UI.UIHandCardPanel FindPanel(GameLogicManager gm)
        {
            try
            {
                var panel = gm != null && gm.battle != null && gm.battle.handCard != null
                    ? gm.battle.handCard.UIObject
                    : null;
                if (panel == null || panel.isDisposed || panel.parent == null) return null;
                return panel;
            }
            catch { return null; }
        }

        /// <summary>
        /// 读取当前玩家（回放中是正在观看的玩家）的手牌：张数与战斗费用总和。
        /// 不经过 <c>Players.HandSnapshot</c> 的“自己账号”判断，回放也能读到；
        /// 只有在拿不到手牌列表时才回退到 SDK 快照。
        /// </summary>
        private static void ReadHand(BattlePlayerData self, out int cards, out int totalCost)
        {
            cards = 0;
            totalCost = 0;
            try
            {
                var hand = self?.cardContainer?._HandCards;
                if (hand != null)
                {
                    cards = hand.Count;
                    for (int i = 0; i < hand.Count; i++)
                    {
                        try
                        {
                            var card = hand[i];
                            if (card == null) continue;
                            int cost = card.BattleCost;
                            if (cost < 0) cost = card.Config != null ? card.Config.Cost : 0;
                            if (cost > 0) totalCost += cost;
                        }
                        catch { }
                    }
                    return;
                }
            }
            catch { }

            try
            {
                long playerId = self != null && self.player != null ? self.player.Id : 0;
                if (playerId == 0) return;
                var hand = Players.HandSnapshot(playerId);
                if (hand == null) return;
                cards = hand.Item1;
                totalCost = hand.Item2;
            }
            catch { }
        }

        private bool Build(UI.UIHandCardPanel panel)
        {
            try
            {
                TextFormat source = null;
                // 优先用底部「出牌 0/1」那行 HUD 文字的字体：它是游戏原生 HUD 字体，
                // 中文和数字都清晰；技能按钮标题可能带按钮专用的描边/膨胀效果，直接复用会发虚。
                try { if (panel.txt_CardState != null) source = panel.txt_CardState.textFormat; }
                catch { }
                if (source == null)
                {
                    try
                    {
                        var skillButton = panel.com_Skill != null ? panel.com_Skill.btn_Skill : null;
                        var skillText = skillButton != null ? skillButton.GetTextField() : null;
                        if (skillText != null) source = skillText.textFormat;
                    }
                    catch { }
                }

                int baseSize = 20;
                if (source != null && source.size > 0)
                    baseSize = Mathf.Clamp(source.size - 2, 14, 48);
                int titleSize = baseSize;
                int detailSize = baseSize;
                _lastTitleSize = titleSize;

                var box = new GComponent { name = "cesium-megas-bombardment", touchable = false, opaque = false };
                var bg = new GGraph { touchable = false };
                box.AddChild(bg);

                // 每段文字一个独立 GTextField，不用 UBB 富文本：
                //   · 字体和颜色都按原生 HUD 文本直接设置，不经过 HTML 解析器；
                //   · 每个变量可以精确给不同颜色，也不会因为标签/字体回退而发糊。
                _titleParts = new[]
                {
                    CreateText(box, "梅加斯 理论伤害", titleSize, Color.white, true, source),
                    CreateText(box, "0", titleSize, TotalColor, true, source),
                };
                _detailParts = new[]
                {
                    CreateText(box, "0", detailSize, CardsColor, true, source),
                    CreateText(box, "张(费", detailSize, LabelColor, false, source),
                    CreateText(box, "0", detailSize, CostColor, true, source),
                    CreateText(box, ") = ", detailSize, LabelColor, false, source),
                    CreateText(box, "0", detailSize, ShotsColor, true, source),
                    CreateText(box, "炮 * ", detailSize, LabelColor, false, source),
                    CreateText(box, "0", detailSize, PerShotColor, true, source),
                    CreateText(box, "伤", detailSize, LabelColor, false, source),
                };
                _zero = CreateText(box, "无法开炮", detailSize, LabelColor, false, source);
                _zero.visible = false;

                panel.AddChild(box);
                _box = box;
                _bg = bg;
                return true;
            }
            catch (Exception e)
            {
                WarnOnce(e);
                return false;
            }
        }

        private void UpdateText(int cards, int totalCost, int shots, int perShot, int total)
        {
            string key = cards + "|" + totalCost + "|" + shots + "|" + perShot + "|" + total;
            if (key == _shownKey) return;
            _shownKey = key;
            if (_titleParts == null || _detailParts == null || _zero == null) return;

            SetText(_titleParts[1], total.ToString());
            SetText(_detailParts[0], cards.ToString());
            SetText(_detailParts[2], totalCost.ToString());

            bool canFire = shots > 0;
            for (int i = 4; i < _detailParts.Length; i++)
                if (_detailParts[i] != null) _detailParts[i].visible = canFire;
            _zero.visible = !canFire;
            if (canFire)
            {
                SetText(_detailParts[4], shots.ToString());
                SetText(_detailParts[6], perShot.ToString());
            }
        }

        private static void SetText(GTextField field, string text)
        {
            if (field != null && !field.isDisposed) field.text = text;
        }

        private void Layout(UI.UIHandCardPanel panel)
        {
            if (_box == null || _box.isDisposed || _anchor == null ||
                _titleParts == null || _detailParts == null || _zero == null) return;

            float baseSize = _lastTitleSize > 0 ? _lastTitleSize : 18f;
            float pad = Mathf.Max(5f, baseSize * 0.34f);
            float gap = Mathf.Max(1f, baseSize * 0.12f);

            // 第一行：标题 + 总伤害；两段之间留一点空隙。
            float titleX = pad;
            float titleWidth = 0f;
            float titleHeight = 0f;
            for (int i = 0; i < _titleParts.Length; i++)
            {
                var part = _titleParts[i];
                if (part == null || part.isDisposed || !part.visible) continue;
                part.SetXY(titleX, pad);
                titleX += part.width;
                if (i == 0) titleX += Mathf.Max(2f, baseSize * 0.12f);
                titleWidth = Mathf.Max(titleWidth, titleX - pad);
                titleHeight = Mathf.Max(titleHeight, part.height);
            }

            // 第二行：手牌 / 费用 / 炮数 / 单炮伤害，每段独立控件。
            float detailY = pad + titleHeight + gap;
            float detailX = pad;
            float detailWidth = 0f;
            float detailHeight = 0f;
            for (int i = 0; i < _detailParts.Length; i++)
            {
                var part = _detailParts[i];
                if (part == null || part.isDisposed || !part.visible) continue;
                part.SetXY(detailX, detailY);
                detailX += part.width;
                detailWidth = Mathf.Max(detailWidth, detailX - pad);
                detailHeight = Mathf.Max(detailHeight, part.height);
            }
            if (_zero.visible)
            {
                _zero.SetXY(detailX, detailY);
                detailWidth = Mathf.Max(detailWidth, detailX + _zero.width - pad);
                detailHeight = Mathf.Max(detailHeight, _zero.height);
            }

            float width = Mathf.Max(titleWidth, detailWidth) + pad * 2f;
            float height = titleHeight + gap + detailHeight + pad * 2f;
            if (Math.Abs(width - _lastWidth) > 0.01f || Math.Abs(height - _lastHeight) > 0.01f)
            {
                _lastWidth = width;
                _lastHeight = height;
                _box.SetSize(width, height);
                _bg.DrawRect(width, height, 1,
                    new Color(0f, 0f, 0f, 0f), new Color(0.02f, 0.03f, 0.05f, 0.84f));
            }

            // 锚点是技能按钮（com_Skill），它和「移动」按钮同在底部手牌面板里；
            // 回放隐藏移动按钮时技能按钮仍在，因此这里不会跟着消失。
            float x = _anchor.x + (_anchor.width - width) * 0.5f;
            float y = _anchor.y - height - AnchorGap;

            float panelWidth = panel.width;
            if (panelWidth > 0f && x + width > panelWidth - EdgePad)
                x = panelWidth - width - EdgePad;
            if (x < EdgePad) x = EdgePad;
            if (y < EdgePad) y = EdgePad;

            _box.SetXY(x, y);
            if (_box.parent != null)
                _box.parent.SetChildIndex(_box, _box.parent.numChildren - 1);
        }

        private static GTextField CreateText(GComponent parent, string text, int size, Color color, bool bold, TextFormat source)
        {
            var field = new GTextField { touchable = false, autoSize = AutoSizeType.Both };
            field.textFormat = BuildFormat(source, size, color, bold);
            field.text = text;
            parent.AddChild(field);
            return field;
        }

        private static TextFormat BuildFormat(TextFormat source, int size, Color color, bool bold)
        {
            var format = new TextFormat();
            // 只继承字体族；不继承按钮标题的膨胀/软垫/渐变/字距等效果，
            // 那些效果会把 HUD 小字糊成一团或让字符互相重叠。
            if (source != null) format.font = source.font;
            format.size = size;
            format.color = color;
            format.bold = bold;
            format.italic = false;
            format.underline = false;
            format.strikethrough = false;
            format.lineSpacing = 0;
            format.letterSpacing = 0;
            format.faceDilate = 0f;
            format.underlaySoftness = 0f;
            format.gradientColor = null;
            format.specialStyle = TextFormat.SpecialStyle.None;
            // 不要黑描边、软描边或阴影，保持干净的 HUD 字体。
            format.outline = 0f;
            format.outlineColor = Color.clear;
            format.outlineSoftness = 0f;
            format.shadowColor = Color.clear;
            format.shadowOffset = Vector2.zero;
            return format;
        }

        private void WarnOnce(Exception e)
        {
            if (_warned) return;
            _warned = true;
            SdkLog.Warn("CombatOdds", "梅加斯理论伤害显示失败: " + e.Message);
        }
    }
}
