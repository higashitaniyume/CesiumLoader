using System;
using CesiumLoader.SDK.Logging;
using FairyGUI;
using UnityEngine;

namespace CombatOddsMod
{
    /// <summary>
    /// 左下角「自己的角色卡」上的移速加成读数: 在 <c>查看筹码</c> 按钮正上方显示
    /// <c>移速+3（+4/-1）</c> 这样一行, 正数绿色、负数红色、没有加减成时灰色 <c>移速0</c>。
    ///
    /// 数据: <c>BattleProperty.ExtraAddMovePoint</c> / <c>ExtraMinusMovePoint</c>。这两个值由
    /// 服务器在 <c>HeroAddMoveChangeS2C</c> 里下发、<c>BattleLogic</c> 写进属性, 游戏自己的棋盘
    /// 名牌(<c>UICom_AttrInfo.txt_Move</c>)显示的也是同一个数; 掷骰后的移动点数 = 骰点 + 净加成。
    /// 本模组只读, 不修改游戏元素。
    ///
    /// 位置: 文本挂在 HUD 面板根节点下, 用「查看筹码」按钮的局部坐标定位到它正上方,
    /// 因此 HUD 面板缩放/滑入滑出时一起跟随; PVP 里游戏会隐藏这个按钮, 这时改放防御数字右侧。
    /// 字号与水平/垂直偏移可在 Toys 配置里微调
    /// (<c>MoveBonusFontSize</c> / <c>MoveBonusOffsetX</c> / <c>MoveBonusOffsetY</c>)。
    ///
    /// 字体: 攻击力数字用的是只含数字的位图字体, 拿它画「移速」只会看到空白或数字
    /// (攻击力加成悬浮框踩过这个坑), 所以从 HUD 上显示 Buff 名称/说明的中文 TrueType
    /// 文本克隆字体格式。
    /// </summary>
    internal sealed class MoveBonusOverlay
    {
        private const string Tag = "CombatOdds.hud";
        private const string NodeName = "cesium-move-bonus";

        /// <summary>读数底边与「查看筹码」按钮顶边之间的间距(像素)。</summary>
        internal static float GapAboveButton = 24f;

        private static readonly Color PositiveColor = new Color32(0x5B, 0xE3, 0x7A, 0xFF);
        private static readonly Color NegativeColor = new Color32(0xFF, 0x6B, 0x6B, 0xFF);
        private static readonly Color ZeroColor = new Color32(0xBF, 0xBF, 0xBF, 0xFF);

        private UI.UIBattleInfoPanel _panel;
        private GObject _anchor;          // 当前锚点(查看筹码按钮或防御数字); 面板重建后失效
        private GTextField _text;
        private double _fontSize = 28;
        private double _offsetX;
        private double _offsetY;
        private bool _layoutLogged;
        private bool _warned;

        /// <summary>配置热重载时同步字号/偏移(下一次刷新生效)。</summary>
        public void SetLayout(double fontSize, double offsetX, double offsetY)
        {
            _fontSize = fontSize > 0 ? fontSize : 28;
            _offsetX = offsetX;
            _offsetY = offsetY;
        }

        /// <summary>
        /// 刷新读数。面板还没出现(不在战局/回放里)时返回 false, 不影响下次再试。
        /// </summary>
        public bool Apply(MoveBonusView view)
        {
            if (!EnsureTarget()) return false;
            try
            {
                if (_panel == null || _panel.isDisposed || !_panel.visible || _panel.parent == null)
                {
                    Hide();
                    return false;
                }

                var parent = _text.parent;
                if (parent == null) return false;

                // 先上色再设文字: GTextField.color 的 setter 会立即重绘, 顺序反了颜色不生效。
                ApplyFormat(ColorFor(view.Tint));
                _text.text = view.Text;

                float w = _text.textWidth > 0f ? _text.textWidth : _text.width;
                float h = _text.textHeight > 0f ? _text.textHeight : _text.height;

                // 坐标必须与锚点同一坐标系: 元素就挂在锚点的父容器下, 直接用锚点的局部 x/y。
                // 绝不能拿 LocalToGlobal 的结果当父容器内的 xy(那是屏幕空间, 差一个缩放)。
                float x, y;
                if (!PlaceAboveRelicButton(parent, w, h, out x, out y))
                    PlaceBesideDefense(parent, w, h, out x, out y);

                _text.SetXY(x, y);
                parent.SetChildIndex(_text, parent.numChildren - 1);   // 盖在同层兄弟之上
                _text.visible = true;

                LogLayoutOnce();
                return true;
            }
            catch (Exception e)
            {
                Warn("画移速加成失败: " + e.Message);
                return false;
            }
        }

        /// <summary>只隐藏, 不销毁元素(离开战局后下一次直接复用)。</summary>
        public void Hide()
        {
            try { if (_text != null) _text.visible = false; } catch { }
        }

        /// <summary>HUD 面板重建/卸载时丢弃元素, 下次重新定位。</summary>
        public void Reset()
        {
            try
            {
                if (_text != null && _text.parent != null) _text.parent.RemoveChild(_text, true);
            }
            catch { }
            _text = null;
            _panel = null;
            _anchor = null;
            _layoutLogged = false;
        }

        private bool EnsureTarget()
        {
            try
            {
                if (_text != null && _text.parent != null && _panel != null && !_panel.isDisposed && _panel.parent != null)
                    return true;

                Reset();
                _panel = HudAtkBonusOverlay.FindPanel();
                if (_panel == null) return false;

                // 直接挂在 HUD 面板根节点下: 『查看筹码』按钮与防御数字都是它的直接子节点,
                // 坐标同系, 不会因为父容器不同而错位。
                _text = new GTextField { name = NodeName, touchable = false, autoSize = AutoSizeType.Both };
                ApplyFormat(ZeroColor);
                _panel.AddChild(_text);
                _text.visible = false;

                if (_panel.btn_ShowRelic == null)
                    SdkLog.Warn(Tag, "[hud] 没找到『查看筹码』按钮(游戏版本变了?), 移速加成改放防御数字右侧");
                return true;
            }
            catch (Exception e)
            {
                Warn("定位移速加成位置失败: " + e.Message);
                return false;
            }
        }

        /// <summary>贴在『查看筹码』按钮正上方(只有 PVE 才会显示这个按钮)。</summary>
        private bool PlaceAboveRelicButton(GComponent parent, float w, float h, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            var btn = _panel != null ? _panel.btn_ShowRelic : null;
            if (btn == null || btn.isDisposed || btn.parent != parent || !btn.visible) return false;

            x = btn.x + (btn.width - w) * 0.5f + (float)_offsetX;
            y = btn.y - h - GapAboveButton + (float)_offsetY;
            _anchor = btn;
            return true;
        }

        /// <summary>PVP 里游戏会隐藏『查看筹码』, 这时改放防御数字右侧。</summary>
        private void PlaceBesideDefense(GComponent parent, float w, float h, out float x, out float y)
        {
            var def = _panel != null ? _panel.txt_DEF : null;
            if (def != null && !def.isDisposed && def.parent == parent)
            {
                x = def.x + def.width + 8f + (float)_offsetX;
                y = def.y + (def.height - h) * 0.5f + (float)_offsetY;
                _anchor = def;
                return;
            }

            // 两个锚点都取不到: 面板底边居中, 至少不会跑到屏幕外。
            x = (parent.width - w) * 0.5f + (float)_offsetX;
            y = parent.height - 48f - h + (float)_offsetY;
            _anchor = null;
        }

        private Color ColorFor(MoveBonusTint tint)
        {
            switch (tint)
            {
                case MoveBonusTint.Positive: return PositiveColor;
                case MoveBonusTint.Negative: return NegativeColor;
                default: return ZeroColor;
            }
        }

        private void ApplyFormat(Color color)
        {
            var tf = CloneFormatFor(SourceFormat());
            if (string.IsNullOrEmpty(tf.font))
            {
                try { tf.font = UIConfig.defaultFont; } catch { }
            }
            tf.size = (int)Math.Round(_fontSize > 0 ? _fontSize : 28);
            if (tf.size < 12) tf.size = 12;
            tf.bold = true;
            tf.outline = 2;
            tf.outlineColor = Color.black;
            tf.color = color;
            _text.textFormat = tf;
        }

        /// <summary>
        /// 取一套能显示中文的字体格式: 优先 HUD 上显示 Buff 名称/说明的文本(那里必定是中文 TrueType)。
        /// </summary>
        private TextFormat SourceFormat()
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
            return null;
        }

        /// <summary>按原格式克隆一份(共用引用会连带改到游戏自己的元素)。</summary>
        private static TextFormat CloneFormatFor(TextFormat src)
        {
            var tf = new TextFormat();
            if (src == null) return tf;
            tf.font = src.font;
            tf.size = src.size;
            tf.bold = src.bold;
            tf.italic = src.italic;
            tf.underline = src.underline;
            tf.strikethrough = src.strikethrough;
            tf.align = src.align;
            tf.lineSpacing = src.lineSpacing;
            tf.letterSpacing = src.letterSpacing;
            tf.outline = src.outline;
            tf.outlineColor = src.outlineColor;
            tf.outlineSoftness = src.outlineSoftness;
            tf.shadowColor = src.shadowColor;
            tf.shadowOffset = src.shadowOffset;
            tf.gradientColor = src.gradientColor;
            tf.faceDilate = src.faceDilate;
            tf.underlaySoftness = src.underlaySoftness;
            tf.specialStyle = src.specialStyle;
            return tf;
        }

        /// <summary>第一次真正画出来时记一行日志(含实际坐标), 方便按日志微调偏移。</summary>
        private void LogLayoutOnce()
        {
            if (_layoutLogged) return;
            _layoutLogged = true;
            try
            {
                string anchorName = _anchor == null ? "面板底边"
                    : (_panel != null && ReferenceEquals(_anchor, _panel.btn_ShowRelic) ? "查看筹码" : "防御数字");
                SdkLog.Info(Tag, "[hud] 移速加成已显示: 文本=" + _text.text
                    + " 元素=(" + _text.x.ToString("0.#") + "," + _text.y.ToString("0.#")
                    + " " + _text.width.ToString("0.#") + "x" + _text.height.ToString("0.#") + ")"
                    + " 锚点=" + anchorName
                    + (_anchor != null ? "(" + _anchor.x.ToString("0.#") + "," + _anchor.y.ToString("0.#")
                        + " " + _anchor.width.ToString("0.#") + "x" + _anchor.height.ToString("0.#") + ")" : string.Empty)
                    + " 字号=" + _text.textFormat.size
                    + " 面板=" + _panel.width.ToString("0.#") + "x" + _panel.height.ToString("0.#"));
            }
            catch { }
        }

        private void Warn(string message)
        {
            if (_warned) return;
            _warned = true;
            SdkLog.Warn(Tag, "[hud] " + message + " (只提示一次)");
        }
    }
}
