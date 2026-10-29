using System;
using System.Reflection;
using FairyGUI;
using Tools;
using UI;
using UnityEngine;

namespace QuickChatPlusMod
{
    /// <summary>
    /// 快捷短语列表在游戏里由 ExpressionWindow 打开时调用 ResizeToFit() 撑到全部条目高度，
    /// 条目一多就会超出屏幕。这里在列表打开期间把可视高度限制在屏幕内、打开滚动
    /// （滚轮 / 拖动），并在列表贴近屏幕底部时整体上移，保证所有条目都能翻到。
    ///
    /// 只操作 FairyGUI 组件的尺寸与 ScrollPane 开关，不改游戏代码、不碰协议。
    /// </summary>
    internal sealed class ChatListFitter
    {
        private const float ScreenMargin = 24f;
        private const float HeightEpsilon = 0.5f;

        private static FieldInfo _windowField;
        private static FieldInfo _listField;
        private static bool _resolved;
        private static bool _warned;

        private readonly Action<string> _warn;
        private ExpressionWindow _window;
        private int _numItems;
        private bool _failed;

        public ChatListFitter(Action<string> warn)
        {
            _warn = warn;
        }

        public void Tick(int maxVisibleItems)
        {
            if (_failed || !ResolveFields()) return;

            UIManager ui;
            ExpressionWindow window;
            GList list;
            ScrollPane pane;
            try
            {
                ui = SimpleSingletonProvider<UIManager>.inst;
                if (ui == null) { Reset(); return; }
                window = (ExpressionWindow)_windowField.GetValue(ui);
                if (window == null || !window.isShowing) { Reset(); return; }
                list = (GList)_listField.GetValue(window);
                if (list == null) { Reset(); return; }
                pane = list.scrollPane;
            }
            catch { return; }

            if (pane == null) { Reset(); return; }
            int count = list.numItems;
            if (count <= 0) { Reset(); return; }

            float content = pane.contentHeight;
            if (content <= 0f) content = list.height;
            if (content <= 0f) return;

            Vector2 topLeft = list.LocalToGlobal(Vector2.zero);
            float screenHeight = GRoot.inst.height;
            float itemHeight = content / count;
            var fit = ListFitMath.Compute(content, count, maxVisibleItems, topLeft.y, screenHeight,
                ScreenMargin, itemHeight * 2f);

            if (fit.ShiftUp > 0f)
            {
                var parent = list.parent;
                float scaleY = parent != null && parent.scaleY > 0.0001f ? parent.scaleY : 1f;
                list.y -= fit.ShiftUp / scaleY;
            }
            if (Math.Abs(list.height - fit.Height) > HeightEpsilon) list.height = fit.Height;

            // 游戏打开列表时把 touchEffect 关了；这里恢复滚轮与拖动。
            pane.touchEffect = true;
            pane.mouseWheelEnabled = true;

            if (!ReferenceEquals(_window, window) || _numItems != count)
            {
                _window = window;
                _numItems = count;
                pane.SetPosY(0f, false);
            }
        }

        /// <summary>停用/卸载时恢复列表为内容全高、关闭拖动，和游戏原版打开时一致。</summary>
        public void Restore()
        {
            var window = _window;
            Reset();
            if (window == null || !_resolved || _listField == null) return;
            try
            {
                var list = (GList)_listField.GetValue(window);
                if (list == null || !window.isShowing || list.numItems <= 0) return;
                var pane = list.scrollPane;
                if (pane == null) return;
                if (Math.Abs(list.height - pane.contentHeight) > HeightEpsilon) list.height = pane.contentHeight;
                pane.touchEffect = false;
            }
            catch { }
        }

        private void Reset()
        {
            _window = null;
            _numItems = 0;
        }

        private bool ResolveFields()
        {
            if (_resolved) return _windowField != null && _listField != null;
            _resolved = true;
            try
            {
                _windowField = typeof(UIManager).GetField("_cachedExpressionWindow",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                _listField = typeof(ExpressionWindow).GetField("_list_Chat",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }
            catch { }

            if (_windowField == null || _listField == null)
            {
                _failed = true;
                if (!_warned)
                {
                    _warned = true;
                    try { _warn("找不到聊天列表的 UI 字段，列表滚动适配未启用。"); } catch { }
                }
                return false;
            }
            return true;
        }
    }
}
