using System;
using System.Collections.Generic;
using System.Reflection;
using FairyGUI;
using GameLogic;
using Tools;
using UI;

namespace ChatDurationMod
{
    /// <summary>
    /// 战斗消息/表情列表（<see cref="ExpressionListWindow"/>）的停留时间适配。
    ///
    /// 游戏给每个条目注册了一个按游戏时间计时的 UnityTimer；本模组先由
    /// <see cref="ExpressionDurationOverride"/> 把该计时压到极大，再在这里按真实时间
    /// 逐条隐藏，因此变速不会缩短停留时间。
    ///
    /// 只操作游戏自己的 FairyGUI 条目，不创建界面、不发送消息、不改对局数据。
    /// </summary>
    internal sealed class ExpressionListLifetime
    {
        private static readonly FieldInfo WindowField = typeof(UIManager).GetField(
            "_cachedExpressionListWindow", BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool _warnedWindowField;
        private static bool _warnedSignal;

        private readonly MessageStayTracker _tracker = new MessageStayTracker();
        private Signal<BattleMessage> _signal;
        private int _pendingNewest;

        /// <summary>按真实时间扫描一次列表；now 与 durationSeconds 的单位都是秒。</summary>
        public void Tick(double now, double durationSeconds, Action<string> warn)
        {
            UIManager ui;
            ExpressionListWindow window;
            GList list;
            if (!TryFindList(out ui, out window, out list, warn))
            {
                Unsubscribe();
                _tracker.Clear();
                return;
            }

            Subscribe(warn);

            int count = list.numItems;
            if (count <= 0)
            {
                _tracker.Clear();
                _pendingNewest = 0;
                return;
            }

            // 新消息会追加到列表末尾；FairyGUI 对象池可能把刚移除的旧条目直接复用给新消息，
            // 所以收到信号后把末尾 N 条重新计时，不能只按对象身份沿用旧时间戳。
            if (_pendingNewest > 0)
            {
                int reset = Math.Min(_pendingNewest, count);
                for (int i = count - reset; i < count; i++)
                {
                    GObject item = SafeChild(list, i);
                    if (IsTarget(item)) _tracker.MarkShown(item, now);
                }
                _pendingNewest = 0;
            }

            var live = new List<object>(count);
            for (int i = 0; i < count; i++)
            {
                GObject item = SafeChild(list, i);
                if (!IsTarget(item)) continue;
                live.Add(item);

                MessageStayTracker.Entry entry = _tracker.Observe(item, now);
                if (entry.Expired)
                {
                    // 已隐藏的条目又被游戏重新显示：当成新消息重新计时。
                    if (!IsVisible(item)) continue;
                    _tracker.MarkShown(item, now);
                }

                if (_tracker.IsDue(item, now, durationSeconds))
                {
                    Hide(item);
                    _tracker.MarkExpired(item);
                }
            }

            _tracker.Prune(live);
        }

        /// <summary>场景切换时丢弃旧对象，避免继续操作已销毁的条目。</summary>
        public void Reset()
        {
            Unsubscribe();
            _tracker.Clear();
        }

        /// <summary>停用/卸载时隐藏所有由本模组延长过的条目，再恢复游戏原计时。</summary>
        public void HideAllAndReset()
        {
            Unsubscribe();

            object[] keys = _tracker.SnapshotKeys();
            for (int i = 0; i < keys.Length; i++)
            {
                GObject item = keys[i] as GObject;
                if (item != null) Hide(item);
            }

            // 兜底：窗口里可能还有本模组没来得及跟踪的条目（例如刚打开窗口时）。
            try
            {
                UIManager ui;
                ExpressionListWindow window;
                GList list;
                if (TryFindList(out ui, out window, out list, null))
                {
                    int count = list.numItems;
                    for (int i = 0; i < count; i++)
                    {
                        GObject item = SafeChild(list, i);
                        if (IsTarget(item)) Hide(item);
                    }
                }
            }
            catch { }

            _tracker.Clear();
        }

        private static bool TryFindList(out UIManager ui, out ExpressionListWindow window,
            out GList list, Action<string> warn)
        {
            ui = null;
            window = null;
            list = null;
            try
            {
                if (!SimpleSingletonProvider<UIManager>.hasInstance) return false;
                ui = SimpleSingletonProvider<UIManager>.inst;
                if (ui == null) return false;

                if (WindowField == null)
                {
                    WarnOnce(ref _warnedWindowField, warn,
                        "找不到游戏的消息列表窗口字段，消息停留时间未生效。");
                    return false;
                }

                window = WindowField.GetValue(ui) as ExpressionListWindow;
                if (window == null || !window.isShowing) return false;

                var pane = window.contentPane as UIExpressionListWindow;
                if (pane == null || pane.list_Plaform == null) return false;

                list = pane.list_Plaform;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Subscribe(Action<string> warn)
        {
            Signal<BattleMessage> signal = null;
            try
            {
                if (SimpleSingletonProvider<GameLogicManager>.hasInstance)
                {
                    GameLogicManager manager = SimpleSingletonProvider<GameLogicManager>.inst;
                    if (manager != null && manager.communicate != null)
                        signal = manager.communicate.Signal.battleMessage;
                }
            }
            catch { }

            if (ReferenceEquals(signal, _signal)) return;

            Unsubscribe();
            _signal = signal;
            if (_signal == null)
            {
                WarnOnce(ref _warnedSignal, warn,
                    "找不到战斗消息信号，连续多条消息时可能提前隐藏。");
                return;
            }

            try { _signal.AddListener(OnBattleMessage); }
            catch { _signal = null; }
        }

        private void Unsubscribe()
        {
            Signal<BattleMessage> signal = _signal;
            _signal = null;
            _pendingNewest = 0;
            if (signal == null) return;
            try { signal.RemoveListener(OnBattleMessage); }
            catch { }
        }

        private void OnBattleMessage(BattleMessage message)
        {
            if (_pendingNewest < 64) _pendingNewest++;
        }

        private static GObject SafeChild(GList list, int index)
        {
            try { return list.GetChildAt(index); }
            catch { return null; }
        }

        private static bool IsTarget(GObject item)
        {
            return item is UIExpression_Com_ChatItem || item is UIExpression_Com_ExpressionItem;
        }

        private static bool IsVisible(GObject item)
        {
            try { return item != null && !item.isDisposed && item.visible; }
            catch { return false; }
        }

        private static void Hide(GObject item)
        {
            if (item == null) return;
            try
            {
                if (!item.isDisposed) item.visible = false;
            }
            catch { }

            try
            {
                var chat = item as UIExpression_Com_ChatItem;
                if (chat != null)
                {
                    chat.Close();
                    return;
                }

                var expression = item as UIExpression_Com_ExpressionItem;
                if (expression != null) expression.Close();
            }
            catch { }
        }

        private static void WarnOnce(ref bool flag, Action<string> warn, string message)
        {
            if (flag) return;
            flag = true;
            if (warn == null) return;
            try { warn(message); }
            catch { }
        }
    }
}
