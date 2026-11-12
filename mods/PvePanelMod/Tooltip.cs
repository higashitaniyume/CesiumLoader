using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using CesiumLoader.SDK.Runtime;
using FairyGUI;
using UnityEngine;

namespace PvePanelMod
{
    /// <summary>挂在图标的 GObject.data 上：鼠标停在这个图标上时显示的说明。</summary>
    internal sealed class Tip
    {
        public string Title;
        public string Body;
        public Tip(string title, string body) { Title = title; Body = body; }
    }

    /// <summary>
    /// 悬停小字：左栏 buff、右栏卡面、右栏筹码、波次表怪物头像；停 0.5 秒才弹，鼠标划过时不会到处闪。
    /// 面板整体不挡鼠标，所以不能用 onRollOver 事件；照 CombatOddsMod.PlayerAttrOverlay 的做法：
    /// 每帧读一次鼠标位置，在我们自己的几个图层里找 data 是 <see cref="Tip"/> 且鼠标落在里面的元素。
    /// </summary>
    internal sealed class Tooltip : IDisposable
    {
        private const float Delay = 0.5f;
        private static readonly string[] Layers = { "PvePanelLeft", "PvePanelRight" };
        private static readonly Color Fill = new Color(0.086f, 0.098f, 0.157f, 0.97f);
        private static readonly Color Edge = new Color(1f, 1f, 1f, 0.22f);

        private GComponent _box;
        private GGraph _border, _bg;
        private GRichTextField _text;   // 富文本：说明里嵌的 <img src='ui://…'/> 小图标要能画出来（和游戏 buff 说明框同款）
        private static readonly Regex ImgSize = new Regex(@"width='\d+'\s+height='\d+'");
        private GObject _hover;
        private float _hoverSince;
        private bool _shown;
        private static MethodInfo _mouseGetter;
        private static bool _mouseProbed;

        public void Tick()
        {
            var root = GRoot.inst;
            if (root == null) return;
            Vector2 mouse;
            if (!TryMouse(out mouse)) { Hide(); return; }

            GObject hit = null;
            for (int i = 0; i < root.numChildren && hit == null; i++)
            {
                var layer = root.GetChildAt(i) as GComponent;
                if (layer == null || !layer.visible || Array.IndexOf(Layers, layer.name) < 0) continue;
                hit = Find(layer, mouse, 0);
            }

            if (hit != _hover) { _hover = hit; _hoverSince = Time.unscaledTime; Hide(); }
            if (_hover == null) return;
            if (!_shown && Time.unscaledTime - _hoverSince >= Delay) Show((Tip)_hover.data, mouse);
            else if (_shown) Place(mouse);
        }

        /// <summary>从后往前找（后加的画在上面），只看可见的元素。</summary>
        private static GObject Find(GObject o, Vector2 mouse, int depth)
        {
            if (o == null || !o.visible || depth > 8) return null;
            if (o is GComponent c)
                for (int i = c.numChildren - 1; i >= 0; i--)
                {
                    var r = Find(c.GetChildAt(i), mouse, depth + 1);
                    if (r != null) return r;
                }
            if (o.data is Tip)
            {
                var p = o.GlobalToLocal(mouse);
                if (p.x >= 0f && p.y >= 0f && p.x <= o.width && p.y <= o.height) return o;
            }
            return null;
        }

        private void Show(Tip tip, Vector2 mouse)
        {
            var root = GRoot.inst;
            float k = PanelScale.K(root);
            if (_box == null || _box.isDisposed || _box.parent != root)
            {
                _box = new GComponent { name = "PvePanelTip", touchable = false, sortingOrder = 25030 };
                _border = new GGraph { touchable = false };
                _bg = new GGraph { touchable = false };
                _text = new GRichTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.Both,
                    UBBEnabled = true,
                    textFormat = new TextFormat { size = Mathf.RoundToInt(15f * k), color = new Color(0.776f, 0.804f, 0.859f) }
                };
                _box.AddChild(_border);
                _box.AddChild(_bg);
                _box.AddChild(_text);
                root.AddChild(_box);
            }
            string body = string.IsNullOrEmpty(tip.Body) ? "" : "\n" + tip.Body;
            // 嵌入的小图标改成和文字一样高
            int icon = Mathf.RoundToInt(20f * k);
            body = ImgSize.Replace(body, "width='" + icon + "' height='" + icon + "'");
            _text.text = "[b][color=#ffffff]" + tip.Title + "[/color][/b]" + body;
            // 太宽的说明折行：超过 360 宽就固定宽度、自动换行
            _text.autoSize = AutoSizeType.Both;
            if (_text.textWidth > 360f * k) { _text.autoSize = AutoSizeType.Height; _text.width = 360f * k; }
            float pad = 10f * k, b = 1.5f * k;
            float w = _text.width + pad * 2, h = _text.textHeight + pad * 2;
            _border.DrawRoundRect(w, h, Edge, new[] { 10f * k, 10f * k, 10f * k, 10f * k });
            _bg.SetXY(b, b);
            _bg.DrawRoundRect(w - 2 * b, h - 2 * b, Fill, new[] { 9f * k, 9f * k, 9f * k, 9f * k });
            _text.SetXY(pad, pad);
            _box.SetSize(w, h);
            _box.visible = true;
            _shown = true;
            Place(mouse);
        }

        /// <summary>出现在鼠标右下方；靠近屏幕边时翻到另一侧，不被切掉。</summary>
        private void Place(Vector2 mouse)
        {
            if (_box == null || _box.isDisposed) return;
            var root = GRoot.inst;
            var p = root.GlobalToLocal(mouse);
            float k = PanelScale.K(root), off = 16f * k;
            float x = p.x + off, y = p.y + off;
            if (x + _box.width > root.width) x = p.x - off - _box.width;
            if (y + _box.height > root.height) y = p.y - off - _box.height;
            _box.SetXY(Mathf.Max(0f, x), Mathf.Max(0f, y));
        }

        public void Hide()
        {
            _shown = false;
            if (_box != null && !_box.isDisposed) _box.visible = false;
        }

        /// <summary>鼠标位置（FairyGUI 的全局坐标 = 屏幕坐标，y 朝下）。编译期没有 Input，运行时按名字找。</summary>
        private static bool TryMouse(out Vector2 mouse)
        {
            mouse = Vector2.zero;
            try
            {
                if (!_mouseProbed)
                {
                    _mouseProbed = true;
                    var t = RuntimeAssemblyService.FindType("UnityEngine.Input") ?? RuntimeAssemblyService.FindTypeBySimpleName("Input");
                    _mouseGetter = t?.GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
                }
                if (_mouseGetter == null) return false;
                var v = (Vector3)_mouseGetter.Invoke(null, null);
                mouse = new Vector2(v.x, Screen.height - v.y);
                return true;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (_box != null && !_box.isDisposed) _box.Dispose();
            _box = null;
        }
    }
}
