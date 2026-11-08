using System;
using FairyGUI;
using UnityEngine;

namespace MapPeekMod
{
    /// <summary>选人阶段左上角地图浮窗（与 ShopRelicPeekMod.Look 同款 FairyGUI 富文本面板，但左上锚定）。</summary>
    internal sealed class MapPeekOverlay
    {
        private const float Padding = 10f;
        private readonly string _name;
        private readonly int _sortingOrder;
        private GComponent _layer;
        private GGraph _bg;
        private GTextField _text;
        private int _lastFontSize = -1;
        private string _lastRenderedText;
        private float _lastPanelWidth;
        private float _lastPanelHeight;
        private float _lastRootWidth;
        private float _lastRootHeight;
        private float _lastOffsetX;
        private float _lastOffsetY;

        public MapPeekOverlay(string name, int sortingOrder)
        {
            _name = name;
            _sortingOrder = sortingOrder;
        }

        public void Clear()
        {
            if (_text != null) { _text.Dispose(); _text = null; }
            if (_bg != null) { _bg.Dispose(); _bg = null; }
            if (_layer != null) { _layer.Dispose(); _layer = null; }
            _lastFontSize = -1;
            _lastRenderedText = null;
            _lastPanelWidth = 0f;
            _lastPanelHeight = 0f;
            _lastRootWidth = 0f;
            _lastRootHeight = 0f;
            _lastOffsetX = 0f;
            _lastOffsetY = 0f;
        }

        public void Hide()
        {
            if (_layer != null && !_layer.isDisposed) _layer.visible = false;
        }

        public void Render(string text, float configuredPanelWidth, float offsetX, float offsetY, int fontSize)
        {
            if (string.IsNullOrEmpty(text)) { Hide(); return; }
            var root = GRoot.inst;
            if (root == null) return;

            EnsureLayer(root);
            EnsureElements(fontSize);

            bool textChanged = text != _lastRenderedText;
            bool rootChanged = Math.Abs(root.width - _lastRootWidth) > 0.5f || Math.Abs(root.height - _lastRootHeight) > 0.5f;
            bool posChanged = Math.Abs(offsetX - _lastOffsetX) > 0.5f || Math.Abs(offsetY - _lastOffsetY) > 0.5f;

            if (textChanged)
            {
                _lastRenderedText = text;
                _text.text = text;
                _lastPanelWidth = Math.Max(configuredPanelWidth, _text.textWidth + Padding * 2f);
                _lastPanelHeight = Math.Max(40f, _text.textHeight + Padding * 2f);
                _bg.DrawRect(_lastPanelWidth, _lastPanelHeight, 1, new Color(1f, 1f, 1f, 0.25f), new Color(0.05f, 0.06f, 0.09f, 0.88f));
            }

            if (textChanged || rootChanged || posChanged)
            {
                _lastRootWidth = root.width;
                _lastRootHeight = root.height;
                _lastOffsetX = offsetX;
                _lastOffsetY = offsetY;
                float x = Math.Max(0f, Math.Min(root.width - _lastPanelWidth, offsetX));
                float y = Math.Max(0f, Math.Min(root.height - _lastPanelHeight, offsetY));
                _bg.SetXY(x, y);
                _text.SetXY(x + Padding, y + Padding);
            }

            if (!_layer.visible) _layer.visible = true;
        }

        private void EnsureLayer(GRoot root)
        {
            if (_layer != null && !_layer.isDisposed && _layer.parent == root) return;
            Clear();
            _layer = new GComponent { name = _name, touchable = false, sortingOrder = _sortingOrder };
            root.AddChild(_layer);
        }

        private void EnsureElements(int fontSize)
        {
            if (_bg == null || _bg.isDisposed)
            {
                _bg = new GGraph { touchable = false };
                _layer.AddChild(_bg);
            }

            int clampedFontSize = Math.Max(12, Math.Min(36, fontSize));
            if (_text == null || _text.isDisposed || _lastFontSize != clampedFontSize)
            {
                if (_text != null && !_text.isDisposed) _text.Dispose();
                _lastFontSize = clampedFontSize;
                _lastRenderedText = null;
                _text = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.Both,
                    UBBEnabled = true
                };
                var tf = new TextFormat
                {
                    size = clampedFontSize,
                    bold = true,
                    color = Color.white,
                    outline = 2f,
                    outlineColor = Color.black
                };
                try
                {
                    if (!string.IsNullOrEmpty(UIConfig.defaultFont)) tf.font = UIConfig.defaultFont;
                }
                catch { }
                _text.textFormat = tf;
                _layer.AddChild(_text);
            }
        }
    }
}
