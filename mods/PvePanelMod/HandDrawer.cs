using System;
using System.Collections.Generic;
using FairyGUI;
using PvePanelMod.Hands;
using UnityEngine;

namespace PvePanelMod
{
    /// <summary>
    /// 手牌抽屉（草图 panel.html 的 .drawer）：贴在左栏右边，每一行和左栏的玩家对齐。
    ///   · 同一张牌只画一张，右上角「×N」；临时牌左下角写「临时」；
    ///   · 7 种以内摆开，8 种以上按 7 张的宽度叠起来（后压前，「×N」挪到左上角）——轮次结束会弃到 8 张，平时极少超过 7 种；
    ///   · 左栏收起时抽屉贴到屏幕边，每行前面多一条楼层色，看颜色就知道是谁的牌；
    ///   · 看不到的手牌（真人队友、没开第三方服务）那一行只画一条「—」。
    /// 整块不挡鼠标。
    /// </summary>
    internal sealed class HandDrawer : IDisposable
    {
        private const float CardSize = 60f, Gap = 8f, Pad = 12f, Border = 3f, StripW = 8f;
        private const int MaxOpen = 7;
        private const float RowW = MaxOpen * CardSize + (MaxOpen - 1) * Gap;

        private static readonly Color PanelColor = new Color(0.086f, 0.098f, 0.157f, 0.94f);
        private static readonly Color BorderColor = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color Well = new Color(0.165f, 0.184f, 0.278f, 1f);
        private static readonly Color Pink = new Color(1f, 0.09f, 0.463f, 1f);
        private static readonly Color Muted = new Color(0.541f, 0.565f, 0.651f, 1f);
        private static readonly Color[] Floor =
        {
            new Color(1f, 0.275f, 0.275f), new Color(0.58f, 1f, 0.275f), new Color(0.263f, 0.525f, 0.957f), new Color(1f, 0.702f, 0.275f)
        };

        private GComponent _layer, _box;
        private float _k;
        private string _lastKey;

        public bool Visible => _layer != null && !_layer.isDisposed && _layer.visible;

        /// <summary>只挪位置（每帧调用，让抽屉跟着左栏的滑动动画走）。</summary>
        public void SetX(float x)
        {
            if (_box != null && !_box.isDisposed && Math.Abs(_box.x - x) > 0.1f) _box.x = x;
        }

        public void Hide()
        {
            if (_layer != null && !_layer.isDisposed) _layer.visible = false;
        }

        /// <param name="x">抽屉左边（界面坐标）。</param>
        /// <param name="rowTops">左栏每一行的顶部（界面坐标），抽屉的行和它对齐。</param>
        public void Render(float x, float top, float height, IList<float> rowTops, float rowHeight,
            IList<PlayerRow> players, Dictionary<long, HandView> hands, bool showStrip)
        {
            var root = GRoot.inst;
            if (root == null || players == null || players.Count == 0) { Hide(); return; }
            float k = PanelScale.K(root);
            if (_layer == null || _layer.isDisposed || _layer.parent != root || Math.Abs(k - _k) > 0.001f)
            {
                Dispose();
                _k = k;
                _layer = new GComponent { name = "PvePanelHands", sortingOrder = 25010, touchable = false };
                root.AddChild(_layer);
                _box = new GComponent { touchable = false };
                _layer.AddChild(_box);
                _lastKey = null;
            }

            // 有变化才重画（牌、楼层、是否显示色条、位置）
            var sb = new System.Text.StringBuilder();
            sb.Append(showStrip).Append('|').Append(top.ToString("0")).Append('|').Append(height.ToString("0"));
            foreach (var p in players)
            {
                sb.Append('|').Append(p.Slot).Append(':');
                HandView v;
                if (hands != null && hands.TryGetValue(p.Id, out v) && v.Known)
                    foreach (var g in v.Groups) sb.Append(g.CardId).Append(g.IsTemp ? "t" : "").Append('x').Append(g.Count).Append(',');
                else sb.Append('?');
            }
            string key = sb.ToString();
            if (key != _lastKey) { _lastKey = key; Draw(top, height, rowTops, rowHeight, players, hands, showStrip); }
            _box.SetXY(x, top);
            if (!_layer.visible) _layer.visible = true;
        }

        private void Draw(float top, float height, IList<float> rowTops, float rowHeight, IList<PlayerRow> players,
            Dictionary<long, HandView> hands, bool showStrip)
        {
            float k = _k, b = Border * k;
            _box.RemoveChildren(0, -1, true);
            float stripSpace = showStrip ? (StripW + 6f) * k : 0f;
            float w = Pad * 2 * k + stripSpace + RowW * k;
            float r = 18f * k;

            var border = new GGraph { touchable = false };
            border.DrawRoundRect(w, height, BorderColor, new[] { r, r, r, r });
            _box.AddChild(border);
            var bg = new GGraph { touchable = false };
            bg.SetXY(b, b);
            bg.DrawRoundRect(w - 2 * b, height - 2 * b, PanelColor, new[] { r - b, r - b, r - b, r - b });
            _box.AddChild(bg);

            for (int i = 0; i < players.Count && i < rowTops.Count; i++)
            {
                var p = players[i];
                float y = rowTops[i] - top;
                float x = Pad * k;
                if (showStrip)
                {
                    var strip = new GGraph { touchable = false };
                    strip.SetXY(x, y + 10f * k);
                    strip.DrawRoundRect(StripW * k, rowHeight - 20f * k, Floor[Mathf.Clamp(p.Slot, 0, 3)], new[] { 4f * k, 4f * k, 4f * k, 4f * k });
                    _box.AddChild(strip);
                    x += stripSpace;
                }

                HandView v;
                if (hands == null || !hands.TryGetValue(p.Id, out v) || !v.Known)
                {
                    var dash = LeftPanel.Text(16f * k, Muted, false);
                    dash.text = "—";
                    dash.SetXY(x, y + (rowHeight - dash.textHeight) / 2f);
                    _box.AddChild(dash);
                    continue;
                }

                int n = v.Groups.Count;
                bool tight = n > MaxOpen;
                // 叠起来时每张往左压多少：总宽固定为 7 张的宽度
                float step = tight ? (RowW - CardSize) / (n - 1) : CardSize + Gap;
                float cy = y + (rowHeight - CardSize * k) / 2f;
                for (int c = 0; c < n; c++)
                {
                    var g = v.Groups[c];
                    float cx = x + c * step * k;
                    var well = new GGraph { touchable = false };
                    well.SetXY(cx, cy);
                    well.DrawRoundRect(CardSize * k, CardSize * k, Well, new[] { 8f * k, 8f * k, 8f * k, 8f * k });
                    _box.AddChild(well);
                    var img = new UI.TextureLoader { touchable = false, fill = FillType.ScaleFree };
                    img.SetSize(CardSize * k, CardSize * k);
                    img.SetXY(cx, cy);
                    img.url = CardUrl(g.CardId);
                    _box.AddChild(img);
                    if (g.IsTemp) Tag(_box, "临时", 11f, new Color(0, 0, 0, 0.78f), Color.white, cx + 3f * k, cy + CardSize * k - 3f * k, false);
                    if (g.Count > 1)
                    {
                        float bx = tight ? cx - 6f * k : cx + CardSize * k + 6f * k;
                        Tag(_box, "×" + g.Count, 14f, Pink, Color.white, bx, cy - 8f * k, !tight);
                    }
                }
            }
            _box.SetSize(w, height);
        }

        /// <summary>小角标：底色圆角块 + 白字。alignRight=true 时 x 是右边界。y 是顶部；bottom 为 true 的「临时」以 y 为底边。</summary>
        private void Tag(GComponent parent, string text, float size, Color bgColor, Color fg, float x, float y, bool alignRight)
        {
            float k = _k;
            var t = LeftPanel.Text(size * k, fg, true);
            t.text = text;
            float w = t.textWidth + 8f * k, h = t.textHeight;
            float left = alignRight ? x - w : x;
            float topY = text == "临时" ? y - h : y;
            var g = new GGraph { touchable = false };
            g.SetXY(left, topY);
            g.DrawRoundRect(w, h, bgColor, new[] { h / 2f, h / 2f, h / 2f, h / 2f });
            parent.AddChild(g);
            t.SetXY(left + 4f * k, topY);
            parent.AddChild(t);
        }

        private static string CardUrl(int id)
        {
            try
            {
                CardInfoConfigure cfg;
                if (StaticConfigure.Card != null && StaticConfigure.Card.InfoDict.TryGetValue(id, out cfg) && cfg != null) return cfg.GetImage();
            }
            catch { }
            return "UT_HandCard_" + id;
        }

        public void Dispose()
        {
            if (_layer != null && !_layer.isDisposed) _layer.Dispose();
            _layer = null; _box = null;
        }
    }
}
