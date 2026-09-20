using System;
namespace CesiumLoader.SDK
{
    /// <summary>
    /// UI 事件(门面形式)。由 <see cref="UiService"/> 在状态变化时触发, 全部在主线程。
    /// </summary>
    public static class UIEvents
    {
        /// <summary>弹出一条通知。</summary>
        public static event Action<ModNotification> NotificationShown;

        /// <summary>窗口打开。</summary>
        public static event Action<ModWindowInfo> WindowOpened;

        /// <summary>窗口关闭(参数为窗口键 ModId/Id)。</summary>
        public static event Action<string> WindowClosed;

        /// <summary>
        /// "是否有 mod UI 打开"发生变化。
        /// 自由相机等可以据此在 UI 打开时暂停输入。
        /// </summary>
        public static event Action<bool> UiOpenStateChanged;

        /// <summary>订阅者总数(诊断用)。</summary>
        public static int SubscriberCount
        {
            get
            {
                int n = 0;
                var a = NotificationShown; if (a != null) n += a.GetInvocationList().Length;
                var b = WindowOpened; if (b != null) n += b.GetInvocationList().Length;
                var c = WindowClosed; if (c != null) n += c.GetInvocationList().Length;
                var d = UiOpenStateChanged; if (d != null) n += d.GetInvocationList().Length;
                return n;
            }
        }

        /// <summary>清空全部订阅。</summary>
        public static void Clear()
        {
            NotificationShown = null;
            WindowOpened = null;
            WindowClosed = null;
            UiOpenStateChanged = null;
        }

        internal static void RaiseNotification(ModNotification notification)
        {
            var handler = NotificationShown;
            if (handler == null) return;
            foreach (Action<ModNotification> d in handler.GetInvocationList())
            {
                try { d(notification); }
                catch (Exception e) { SdkLog.ReportCrash("UIEvents", "NotificationShown", e); }
            }
        }

        internal static void RaiseWindowOpened(ModWindowInfo window)
        {
            var handler = WindowOpened;
            if (handler == null) return;
            foreach (Action<ModWindowInfo> d in handler.GetInvocationList())
            {
                try { d(window); }
                catch (Exception e) { SdkLog.ReportCrash("UIEvents", "WindowOpened", e); }
            }
        }

        internal static void RaiseWindowClosed(string key)
        {
            var handler = WindowClosed;
            if (handler == null) return;
            foreach (Action<string> d in handler.GetInvocationList())
            {
                try { d(key); }
                catch (Exception e) { SdkLog.ReportCrash("UIEvents", "WindowClosed", e); }
            }
        }

        internal static void RaiseUiOpenStateChanged(bool open)
        {
            var handler = UiOpenStateChanged;
            if (handler == null) return;
            foreach (Action<bool> d in handler.GetInvocationList())
            {
                try { d(open); }
                catch (Exception e) { SdkLog.ReportCrash("UIEvents", "UiOpenStateChanged", e); }
            }
        }
    }
}
