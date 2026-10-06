using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;
using FairyGUI;
using UnityEngine;

namespace CombatOddsMod
{
    /// <summary>
    /// 在左下角主 HUD(<c>UI.UIBattleInfoPanel</c>)的「攻击力」<b>旁边</b>显示筹码加成, 鼠标悬浮显示明细浮框。
    ///
    /// <b>绝不修改游戏自己的攻击力元素(txt_ATK)</b>: 我们另建元素摆在它右边, 每个筹码一个 "+N"
    /// (星币锤/手电筒黄色, 美工刀绿色), 鼠标移上去弹出说明浮框。
    ///
    /// 为什么每个 "+N" 要套一层 <see cref="GComponent"/> 容器:
    ///   真机实测"把 onRollOver 挂在裸 GTextField 上收不到事件" —— 游戏自己也只在
    ///   GComponent/GButton/GLoader 上挂悬浮事件。容器设 <c>opaque=true</c> 后, 它自己的矩形
    ///   就是命中目标, 不依赖子文字的命中测试。
    ///
    /// 为什么要挂到 <c>GRoot</c> 而不是 HUD 面板里:
    ///   面板不是 GRoot 的最上层子节点(上面还有卡片/提示等窗口), 放在面板里的元素会被这些
    ///   更高层的 UI 挡住命中测试。挂到 GRoot 并每次抬高到最顶层, 命中测试才会先命中我们。
    ///
    /// 双保险: 除悬浮事件外, 还有一条自己算鼠标位置的轮询(见 <see cref="OnHoverPoll"/>),
    /// 完全不依赖 FairyGUI 的命中测试。两条路任一命中都会弹浮框。
    ///
    /// FairyGUI 的类型(<c>GTextField</c>/<c>GGraph</c>/<c>GRoot</c>/<c>Stage</c>…)其实就编在
    /// <c>AstralParty.Runtime</c> 里, 所以这里全部<b>强类型引用</b>, 成员名由编译器检查 ——
    /// 再也不会出现"改了属性但没生效/方法名不存在"那种哑失败。
    /// </summary>
    internal sealed class HudAtkBonusOverlay
    {
        private const string Tag = "CombatOdds.hud";

        /// <summary>攻击力数字与第一个 "+N" 之间的水平间距(像素)。</summary>
        internal static float GapX = 6f;
        /// <summary>相邻两个 "+N" 之间的水平间距(像素)。</summary>
        internal static float GapBetween = 6f;
        /// <summary>轮询间隔(秒)。0.05 ≈ 20Hz, 悬浮手感够用。</summary>
        internal static float PollInterval = 0.05f;

        private static readonly Color Yellow = new Color32(0xFF, 0xD2, 0x4A, 0xFF);   // 星币锤 / 手电筒
        private static readonly Color Green = new Color32(0x5B, 0xE3, 0x7A, 0xFF);    // 美工刀

        // ---- 说明浮框外观 ----
        private static readonly Color TipBgColor = new Color(0.05f, 0.05f, 0.08f, 0.94f);
        private static readonly Color TipBorderColor = new Color(1f, 1f, 1f, 0.30f);
        private const int TipFontSize = 22;
        private const float TipPadX = 10f;
        private const float TipPadY = 8f;
        private const float TipMinWidth = 120f;
        private const float TipGap = 8f;
        /// <summary>浮框的 sortingOrder: 必须大于任何 HUD 面板的 Layer, 否则会被面板盖住。</summary>
        private const int TipSortingOrder = 30000;
        private int _tipShownLogged;

        private sealed class BonusBox
        {
            public GComponent Box;
            public GTextField Text;
        }

        private UI.UIBattleInfoPanel _panel;
        private GTextField _txtAtk;                                // 游戏的攻击力字段(只读位置, 从不写它)
        private readonly List<BonusBox> _boxes = new List<BonusBox>();

        private GComponent _tipBox;
        private GGraph _tipBg;
        private GTextField _tipText;
        private BonusBox _tipOwner;
        private bool _polling;
        private bool _scaleLogged;

        /// <summary>是否已经拿到 HUD 攻击力字段。</summary>
        public bool IsReady
        {
            get { return _txtAtk != null; }
        }

        /// <summary>开启自己算鼠标位置的轮询兜底(幂等)。</summary>
        public void StartHoverPolling()
        {
            if (_polling) return;
            try
            {
                Timers.inst.Add(PollInterval, 0, OnHoverPoll);
                _polling = true;
                SdkLog.Info("CombatOdds", "[hud] 悬浮轮询已启动(间隔 " + PollInterval + "s)");
            }
            catch (Exception e)
            {
                SdkLog.Warn("CombatOdds", "[hud] 启动悬浮轮询失败(仅靠悬浮事件): " + e.Message);
            }
        }

        /// <summary>
        /// 在攻击力右边显示<b>一个</b>数字(最终攻击总值 = 基础 + 各筹码加成);
        /// 鼠标悬浮时弹出说明浮框, 讲清这个数是怎么加出来的(带颜色的 +N 明细)。
        /// text 为空时把元素隐藏。
        /// </summary>
        public bool Apply(string text, Color color, string tip)
        {
            if (!EnsureTarget()) return false;
            try
            {
                if (string.IsNullOrEmpty(text) || !PanelShowing()) { HideAll(); return false; }

                // 坐标必须与 txt_ATK 同一坐标系 —— 元素就挂在 txt_ATK 的父节点下,
                // 直接用它的局部 x/y。千万不要把 LocalToGlobal 的结果当父容器内的 xy:
                // 那是"全局/屏幕"空间, 与容器局部空间差一个缩放, 会把元素摆到屏幕外(踩过这个坑)。
                var parent = _txtAtk.parent;
                float atkW = _txtAtk.textWidth > 0f ? _txtAtk.textWidth : _txtAtk.width;
                float atkH = _txtAtk.height;

                var item = EnsureBox(0, parent);
                if (item == null) return false;

                // 先上色再设文字(GTextField.color 的 setter 会立即重绘, 顺序反了颜色不生效)
                item.Text.color = color;
                item.Text.text = text;
                item.Box.data = tip;
                item.Box.visible = true;

                float w = item.Text.textWidth > 0f ? item.Text.textWidth : item.Text.width;
                float h = item.Text.textHeight > 0f ? item.Text.textHeight : atkH;
                item.Box.SetSize(w, h);
                item.Box.SetXY(_txtAtk.x + atkW + GapX, _txtAtk.y + (atkH - h) * 0.5f);

                // 只留这一个元素; 旧版本可能建过多个, 一并隐藏
                for (int i = 1; i < _boxes.Count; i++) _boxes[i].Box.visible = false;

                RaiseWithinParent(parent, 1);
                return true;
            }
            catch (Exception e)
            {
                SdkLog.Warn("CombatOdds", "[hud] 画加成失败: " + e.Message);
                return false;
            }
        }

        /// <summary>面板消失/离开战斗时清缓存, 下次重新定位(把我们建的元素一并丢弃)。</summary>
        public void Reset()
        {
            HideTip();
            try
            {
                for (int i = 0; i < _boxes.Count; i++)
                {
                    var b = _boxes[i].Box;
                    if (b != null && b.parent != null) b.parent.RemoveChild(b, true);
                }
            }
            catch { }
            _boxes.Clear();
            _panel = null;
            _txtAtk = null;
        }

        /// <summary>确保拿到当前活跃的 HUD 攻击力字段; 面板重建时我们的元素要跟着重建。</summary>
        public bool EnsureTarget()
        {
            try
            {
                if (_txtAtk != null && _txtAtk.parent != null && BoxesAlive()) return true;

                Reset();
                _panel = FindPanel();
                if (_panel == null) return false;

                _txtAtk = _panel.txt_ATK;
                if (_txtAtk == null)
                {
                    SdkLog.Warn("CombatOdds", "[hud] 找到 BattleInfoPanel 但没有 txt_ATK 字段(游戏版本变了?)");
                    return false;
                }
                SdkLog.Info("CombatOdds", "[hud] 已定位主 HUD 攻击力字段, 将在其右侧显示筹码加成(可悬浮查看明细)");
                return true;
            }
            catch (Exception e)
            {
                SdkLog.Warn("CombatOdds", "[hud] 定位 HUD 攻击力字段失败: " + e.Message);
                return false;
            }
        }

        private bool PanelShowing()
        {
            try { return _panel != null && _panel.visible && _panel.parent != null && _txtAtk.parent != null; }
            catch { return false; }
        }

        private bool BoxesAlive()
        {
            for (int i = 0; i < _boxes.Count; i++)
                if (_boxes[i].Box == null || _boxes[i].Box.parent == null) return false;
            return true;
        }

        private void HideAll()
        {
            for (int i = 0; i < _boxes.Count; i++) _boxes[i].Box.visible = false;
            HideTip();
        }

        /// <summary>把我们的元素抬到父容器的最上层(同一坐标系内, 不会被同层兄弟盖住)。</summary>
        private void RaiseWithinParent(GComponent parent, int used)
        {
            try
            {
                if (parent == null) return;
                for (int i = 0; i < used && i < _boxes.Count; i++)
                    if (_boxes[i].Box != null) parent.SetChildIndex(_boxes[i].Box, parent.numChildren - 1);
            }
            catch { }
        }

        // ------------------------------------------------------------------ "+N" 元素

        private BonusBox EnsureBox(int index, GComponent parent)
        {
            while (_boxes.Count <= index)
            {
                var item = CreateBox(parent);
                if (item == null) return null;
                _boxes.Add(item);
            }
            return _boxes[index];
        }

        private BonusBox CreateBox(GComponent parent)
        {
            try
            {
                var box = new GComponent();
                box.opaque = true;          // 容器自身矩形即命中目标(裸 GTextField 收不到悬浮事件)
                box.touchable = true;
                box.onRollOver.Add(OnBoxRollOver);
                box.onRollOut.Add(OnBoxRollOut);

                var text = new GTextField();
                text.touchable = false;     // 让事件落到容器上
                text.autoSize = AutoSizeType.Both;
                text.textFormat = CloneFormatFor(_txtAtk.textFormat);
                box.AddChild(text);

                // 挂在 txt_ATK 的同一个父容器下 —— 坐标同系, 位置才和攻击力数字严丝合缝。
                parent.AddChild(box);
                box.visible = false;
                return new BonusBox { Box = box, Text = text };
            }
            catch (Exception e)
            {
                SdkLog.Warn("CombatOdds", "[hud] 创建 +N 元素失败: " + e.Message);
                return null;
            }
        }

        /// <summary>按原格式克隆一份(必须新建, 共用引用会连带改到游戏自己的元素)。</summary>
        private static TextFormat CloneFormatFor(TextFormat src)
        {
            var tf = new TextFormat();
            if (src != null)
            {
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
            }
            return tf;
        }

        // ------------------------------------------------------------------ 悬浮: 事件路径

        private void OnBoxRollOver(EventContext ctx)
        {
            try
            {
                var box = ctx != null ? ctx.sender as GComponent : null;
                if (box == null) return;
                ShowTip(box, box.data as string);
            }
            catch { }
        }

        private void OnBoxRollOut(EventContext ctx)
        {
            HideTip();
        }

        // ------------------------------------------------------------------ 悬浮: 轮询兜底(不依赖命中测试)

        /// <summary>
        /// 自己算鼠标位置: 鼠标落在哪个 "+N" 的矩形里就弹哪个的说明。
        /// 这条路径完全不经过 FairyGUI 的命中测试, 所以即使事件收不到也能弹。
        ///
        /// 鼠标位置优先取 <b>Unity 原始坐标</b>(Input.mousePosition, 左上原点):
        /// 实测本作的 <c>Stage.inst.touchPosition</c> 常年不变(游戏没把鼠标移动喂给 FairyGUI 的 Stage),
        /// 只靠它会永远命中不了。contentScaleFactor != 1 时再换算回 FairyGUI 全局坐标。
        /// </summary>
        private void OnHoverPoll(object param)
        {
            try
            {
                if (_boxes.Count == 0 || !PanelShowing()) return;

                if (!_scaleLogged)
                {
                    _scaleLogged = true;
                    SdkLog.Info("CombatOdds", "[hud] 轮询坐标参考: contentScaleFactor=" + GRoot.contentScaleFactor
                        + " 屏幕=" + Screen.width + "x" + Screen.height);
                }

                var hit = HitBox(UnityPointer());
                if (hit == null) hit = HitBox(Stage.inst.touchPosition);   // 兜底: FairyGUI 自己的指针
                if (hit != null) ShowTip(hit.Box, hit.Box.data as string);
                else if (_tipOwner != null) HideTip();

                DiagNearPointer();
            }
            catch { }
        }

        private static System.Reflection.PropertyInfo _mouseProp;
        private static bool _mouseProbeDone;

        /// <summary>
        /// Unity 鼠标位置 → FairyGUI 全局坐标(左上原点, 按 contentScaleFactor 换算)。
        /// <c>UnityEngine.Input</c> 在独立的 InputLegacyModule 里(mod 没有引用它), 所以用反射读;
        /// 热更程序集不能 P/Invoke, 也就没法直接调 GetCursorPos, 这是唯一可靠来源。
        /// </summary>
        private static Vector2 UnityPointer()
        {
            try
            {
                if (!_mouseProbeDone)
                {
                    _mouseProbeDone = true;
                    var t = RuntimeAssemblyService.FindType("UnityEngine.Input")
                            ?? RuntimeAssemblyService.FindTypeBySimpleName("Input");
                    _mouseProp = t?.GetProperty("mousePosition",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (_mouseProp == null)
                        SdkLog.Warn("CombatOdds", "[hud] 读不到 UnityEngine.Input.mousePosition, 悬浮只能靠 touchPosition");
                }
                if (_mouseProp == null) return new Vector2(-1f, -1f);

                object raw = _mouseProp.GetValue(null);
                if (!(raw is Vector3 m)) return new Vector2(-1f, -1f);

                var p = new Vector2(m.x, Screen.height - m.y);   // 左下原点 → 左上原点
                float cs = GRoot.contentScaleFactor;
                if (cs > 0f && (cs < 0.999f || cs > 1.001f)) { p.x /= cs; p.y /= cs; }
                return p;
            }
            catch { return new Vector2(-1f, -1f); }
        }

        private int _pollCount;
        private Vector2 _lastDiagPointer = new Vector2(-9999f, -9999f);

        /// <summary>
        /// 临时诊断: 鼠标靠近 "+N" 时(每 2 秒最多一条)把"两个鼠标源 + 元素矩形 + 当前命中目标"打进日志,
        /// 用来确认坐标系与是否有别的东西挡住了命中。稳定后可以删掉。
        /// </summary>
        private void DiagNearPointer()
        {
            if (++_pollCount % 40 != 0) return;                  // 0.05s × 40 ≈ 2s
            var b = _boxes[0];
            if (b == null || b.Box == null) return;

            Vector2 up = UnityPointer();
            if (_lastDiagPointer.x == up.x && _lastDiagPointer.y == up.y) return;   // 鼠标没动就不打
            _lastDiagPointer = up;

            Vector2 g = b.Box.LocalToGlobal(Vector2.zero);
            if (Math.Abs(up.x - g.x) > 500f || Math.Abs(up.y - g.y) > 500f) return; // 离得远不记

            var target = GRoot.inst.touchTarget;
            SdkLog.Info("CombatOdds", "[hud] 悬浮诊断: Unity鼠标=" + up
                + " | touchPosition=" + Stage.inst.touchPosition
                + " | 元素0=(" + g.x + "," + g.y + " " + b.Box.width + "x" + b.Box.height + ")"
                + " | touchTarget=" + (target != null ? target.GetType().Name : "null"));
        }

        /// <summary>鼠标位置是否落在某个 "+N" 的矩形内(用 global 坐标比较)。</summary>
        private BonusBox HitBox(Vector2 globalPoint)
        {
            for (int i = 0; i < _boxes.Count; i++)
            {
                var item = _boxes[i];
                if (item.Box == null || !item.Box.visible) continue;
                Vector2 g = item.Box.LocalToGlobal(Vector2.zero);
                float w = item.Box.width;
                float h = item.Box.height;
                if (w <= 0f || h <= 0f) continue;
                if (globalPoint.x >= g.x && globalPoint.x <= g.x + w &&
                    globalPoint.y >= g.y && globalPoint.y <= g.y + h)
                    return item;
            }
            return null;
        }

        // ------------------------------------------------------------------ 说明浮框

        private void ShowTip(GComponent owner, string text)
        {
            if (owner == null || string.IsNullOrEmpty(text)) return;
            try
            {
                EnsureTipBox();
                if (_tipBox == null || _tipText == null || _tipBg == null) return;

                _tipText.text = text;

                float w = _tipText.textWidth + TipPadX * 2f;
                float h = _tipText.textHeight + TipPadY * 2f;
                if (w < TipMinWidth) w = TipMinWidth;

                _tipBg.DrawRect(w, h, 1, TipBorderColor, TipBgColor);
                _tipBox.SetSize(w, h);
                _tipText.SetXY(TipPadX, TipPadY);

                // 摆在悬停元素上方; 上方放不下就放下方; 并夹在屏幕内。
                // 浮框挂在 GRoot 下, 所以必须把"全局坐标"换算成 GRoot 局部坐标(GlobalToLocal),
                // 不能直接拿 LocalToGlobal 的结果当 xy —— 两者差一个缩放(踩过这个坑)。
                var root = GRoot.inst;
                Vector2 ownerGlobal = owner.LocalToGlobal(Vector2.zero);
                Vector2 ownerLocal = root.GlobalToLocal(ownerGlobal);

                float x = ownerLocal.x;
                float y = ownerLocal.y - h - TipGap;
                if (y < 4f) y = ownerLocal.y + owner.height + TipGap;
                if (x + w > root.width - 4f) x = root.width - w - 4f;
                if (x < 4f) x = 4f;
                if (y + h > root.height - 4f) y = root.height - h - 4f;
                if (y < 4f) y = 4f;

                _tipBox.SetXY(x, y);
                _tipBox.visible = true;
                // ★ 关键: FairyGUI 的 GComponent 子节点是按 sortingOrder 排序渲染的, 不是按添加顺序。
                //   HUD 面板在 BasePanel.Show() 里被设了 ui.sortingOrder = config.Layer,
                //   我们的浮框默认 0 会被画在面板"下面" —— 明明显示了却看不见。
                //   给一个远大于任何面板的值, 保证浮框永远在最上层。
                _tipBox.sortingOrder = TipSortingOrder;
                root.SetChildIndex(_tipBox, root.numChildren - 1);
                _tipOwner = new BonusBox { Box = owner };

                // 诊断: 前几次弹出时把几何与可见性记下来(只在最初几条, 不刷屏)
                if (_tipShownLogged < 3)
                {
                    _tipShownLogged++;
                    SdkLog.Info("CombatOdds", "[hud] 浮框已显示: owner局部=(" + ownerLocal.x + "," + ownerLocal.y
                        + ") 浮框=(" + x + "," + y + " " + w + "x" + h + ")"
                        + " root=" + root.width + "x" + root.height
                        + " sortingOrder=" + _tipBox.sortingOrder
                        + " visible=" + _tipBox.visible
                        + " 文字宽高=" + _tipText.textWidth + "x" + _tipText.textHeight
                        + " 文字=" + _tipText.text);
                }
            }
            catch (Exception e)
            {
                SdkLog.Warn("CombatOdds", "[hud] 显示说明浮框失败: " + e.Message);
            }
        }

        private void HideTip()
        {
            try { if (_tipBox != null) _tipBox.visible = false; } catch { }
            _tipOwner = null;
        }

        private void EnsureTipBox()
        {
            if (_tipBox != null && _tipBox.parent != null) return;
            try
            {
                _tipBox = new GComponent();
                _tipBox.touchable = false;      // 浮框不抢鼠标, 免得把 rollOut 吃掉

                _tipBg = new GGraph();
                _tipBox.AddChild(_tipBg);

                _tipText = new GTextField();
                _tipText.touchable = false;
                _tipText.autoSize = AutoSizeType.Both;
                // ★ 字体必须选"能显示中文"的那套: 攻击力数字用的是只含数字的位图字体,
                //   拿它来画中文只会看到数字(真机踩过)。这里取 HUD 上显示 Buff 名称/说明的
                //   文字元素(必定是中文 TrueType), 取不到再退回 FairyGUI 的默认字体。
                var tf = CloneFormatFor(TipSourceFormat());
                if (string.IsNullOrEmpty(tf.font))
                {
                    try { tf.font = UIConfig.defaultFont; } catch { }
                }
                tf.size = TipFontSize;
                tf.color = Color.white;
                tf.outline = 0f;
                _tipText.textFormat = tf;
                // 富文本: 说明里要用 [color=#RRGGBB] 把每个筹码的 +N 染成自己的颜色。
                // (游戏自己的筹码说明就是这么渲染的, 所以这条路是通的)
                _tipText.UBBEnabled = true;
                _tipBox.AddChild(_tipText);

                GRoot.inst.AddChild(_tipBox);
                _tipBox.sortingOrder = TipSortingOrder;   // 保证盖在 HUD 面板之上
                _tipBox.visible = false;
            }
            catch (Exception e)
            {
                SdkLog.Warn("CombatOdds", "[hud] 创建说明浮框失败: " + e.Message);
                _tipBox = null;
            }
        }

        /// <summary>
        /// 给浮框挑一套"能显示中文"的字体格式: 优先 HUD 上显示 Buff 名称/说明的文字元素
        /// (那里必然是中文 TrueType), 取不到返回 null(调用方会退回 UIConfig.defaultFont)。
        /// </summary>
        private TextFormat TipSourceFormat()
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

        // ------------------------------------------------------------------ 定位

        private static UI.UIBattleInfoPanel FindPanel()
        {
            try
            {
                var root = GRoot.inst;
                return root == null ? null : Walk(root, 0);
            }
            catch { return null; }
        }

        private static UI.UIBattleInfoPanel Walk(GObject node, int depth)
        {
            if (node == null || depth > 12) return null;
            if (node is UI.UIBattleInfoPanel panel) return panel;
            if (node is GComponent comp)
            {
                int n = comp.numChildren;
                for (int i = 0; i < n; i++)
                {
                    var found = Walk(comp.GetChildAt(i), depth + 1);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
