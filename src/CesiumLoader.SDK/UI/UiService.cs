using System;
using System.Collections.Generic;
using System.Threading;
namespace CesiumLoader.SDK
{
    /// <summary>UI 状态快照(诊断输出用, 可 JSON 序列化)。</summary>
    [Serializable]
    public sealed class UiState
    {
        /// <summary>是否安装了渲染后端。</summary>
        public bool RenderingAvailable;

        /// <summary>渲染后端名。</summary>
        public string BackendName;

        /// <summary>通知条数。</summary>
        public int NotificationCount;

        /// <summary>窗口总数。</summary>
        public int WindowCount;

        /// <summary>当前打开的窗口数。</summary>
        public int OpenWindowCount;

        /// <summary>覆盖层总数。</summary>
        public int OverlayCount;

        /// <summary>可见覆盖层数。</summary>
        public int VisibleOverlayCount;

        /// <summary>GUI 回调数。</summary>
        public int GuiCallbackCount;

        /// <summary>是否有 mod UI 打开(会屏蔽游戏输入)。</summary>
        public bool AnyUiOpen;

        /// <summary>打开的窗口名列表。</summary>
        public List<string> OpenWindows = new List<string>();

        /// <summary>文本描述。</summary>
        public override string ToString()
        {
            return "UI[rendering=" + RenderingAvailable + " backend=" + (BackendName ?? "(无)") +
                   " notify=" + NotificationCount + " windows=" + OpenWindowCount + "/" + WindowCount +
                   " overlays=" + VisibleOverlayCount + "/" + OverlayCount +
                   " gui=" + GuiCallbackCount + " blockingInput=" + AnyUiOpen + "]";
        }
    }

    /// <summary>
    /// 轻量 Mod UI 服务: 通知 / 覆盖层 / 窗口 / GUI 回调 / 输入屏蔽。
    ///
    /// 定位: <b>状态与生命周期框架</b>, 不是另一套 Unity UI。
    ///  - 不安装 <see cref="IUiBackend"/> 时, 所有登记/查询/层级/输入屏蔽/诊断照常工作,
    ///    通知同时写入日志(方便在没有渲染后端时依然可观察)。
    ///  - 安装后端(内置 IMGUI 宿主或游戏 FairyGUI 适配层)后, 会在渲染时机回调后端绘制。
    ///
    /// 与输入的关系: <see cref="IsAnyWindowOrOverlayOpen"/> 为真时
    /// <see cref="ShouldBlockGameInput"/> 也为真(自由相机等 mod 据此暂停输入响应,
    /// 避免"UI 打开还在转视角"); 但临时通知 <b>不</b> 屏蔽输入, 见
    /// <see cref="ShouldBlockGameInput"/> 的注释。
    ///
    /// 线程: 全部 API 必须在主线程调用(内部会断言并记录一次警告)。
    /// </summary>
    public static class UiService
    {
        private static readonly object _lock = new object();

        private static readonly List<ModNotification> _notifications = new List<ModNotification>();
        private static readonly List<ModWindowInfo> _windows = new List<ModWindowInfo>();
        private static readonly List<ModOverlayInfo> _overlays = new List<ModOverlayInfo>();

        private static readonly List<GuiEntry> _guiCallbacks = new List<GuiEntry>();
        private static Action[] _guiSnapshot = new Action[0];

        private static IUiBackend _backend;
        private static int _installed;
        private static int _windowOrder;
        private static long _nextNotificationId;
        private static long _notifyCount;
        private static long _windowOpenCount;
        private static long _expiredCount;

        private sealed class GuiEntry
        {
            public long Id;
            public Action Callback;
            public ModContext Owner;
            public string Tag;
        }

        // =====================================================================
        // 渲染后端
        // =====================================================================

        /// <summary>当前渲染后端(未安装时为 null)。</summary>
        public static IUiBackend Backend { get { return _backend; } }

        /// <summary>是否具备屏幕渲染能力。</summary>
        public static bool IsRenderingAvailable
        {
            get
            {
                var backend = _backend;
                if (backend == null) return false;
                try { return backend.IsAvailable; }
                catch { return false; }
            }
        }

        /// <summary>安装渲染后端。返回是否安装成功。</summary>
        public static bool TryInstallBackend(IUiBackend backend)
        {
            if (backend == null) return false;

            EnsureInstalled();

            try
            {
                if (!backend.IsAvailable)
                {
                    SdkLog.Warn("UI", "UI 后端不可用: " + backend.Name);
                    return false;
                }

                if (!backend.Install())
                {
                    SdkLog.Warn("UI", "UI 后端安装失败: " + backend.Name);
                    return false;
                }

                _backend = backend;
                SdkLog.Info("UI", "UI 渲染后端已安装: " + backend.Name);
                return true;
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash("UI", "安装 UI 后端 " + backend.Name, e);
                return false;
            }
        }

        /// <summary>卸载渲染后端。</summary>
        public static void UninstallBackend()
        {
            var backend = _backend;
            _backend = null;
            if (backend == null) return;
            try { backend.Uninstall(); } catch { }
            SdkLog.Info("UI", "UI 渲染后端已卸载");
        }

        // =====================================================================
        // 通知
        // =====================================================================

        /// <summary>累计通知条数。</summary>
        public static long NotificationTotal { get { return Interlocked.Read(ref _notifyCount); } }

        /// <summary>当前通知条数。</summary>
        public static int NotificationCount { get { lock (_lock) { return _notifications.Count; } } }

        /// <summary>当前通知(只读快照)。</summary>
        public static IList<ModNotification> Notifications
        {
            get { lock (_lock) { return _notifications.ToArray(); } }
        }

        /// <summary>
        /// 弹出一条通知。即使没有渲染后端, 也会记一条日志。
        /// </summary>
        /// <param name="text">文本。</param>
        /// <param name="level">级别。</param>
        /// <param name="ttl">存活秒数(&lt;=0 表示常驻直到清除)。</param>
        /// <param name="owner">归属 mod。</param>
        public static void Notify(string text, UiNotificationLevel level = UiNotificationLevel.Info,
            float ttl = 3f, ModContext owner = null)
        {
            if (string.IsNullOrEmpty(text)) return;

            MainThreadGuard("Notify");
            EnsureInstalled();

            var resolved = owner ?? SafeCurrentContext();
            var notification = new ModNotification
            {
                Id = Interlocked.Increment(ref _nextNotificationId),
                ModId = resolved != null ? resolved.ModId : "SDK",
                Text = text,
                Level = level,
                CreatedAt = Now(),
                Ttl = ttl
            };

            lock (_lock) { _notifications.Add(notification); }
            Interlocked.Increment(ref _notifyCount);

            try { UIEvents.RaiseNotification(notification); } catch { }

            // 无渲染后端时也保证可观察
            string line = "[通知] " + text;
            switch (level)
            {
                case UiNotificationLevel.Warning: SdkLog.Warn(notification.ModId, line); break;
                case UiNotificationLevel.Error: SdkLog.Error(notification.ModId, line); break;
                case UiNotificationLevel.Success: SdkLog.Info(notification.ModId, line); break;
                default: SdkLog.Info(notification.ModId, line); break;
            }
        }

        /// <summary>清除某个 mod 的通知(不传则清空全部)。</summary>
        public static int ClearNotifications(ModContext owner = null)
        {
            lock (_lock)
            {
                int removed;
                if (owner == null)
                {
                    removed = _notifications.Count;
                    _notifications.Clear();
                }
                else
                {
                    removed = _notifications.RemoveAll(n => string.Equals(n.ModId, owner.ModId, StringComparison.Ordinal));
                }
                return removed;
            }
        }

        // =====================================================================
        // 窗口
        // =====================================================================

        /// <summary>窗口总数。</summary>
        public static int WindowCount { get { lock (_lock) { return _windows.Count; } } }

        /// <summary>当前打开的窗口数。</summary>
        public static int OpenWindowCount
        {
            get
            {
                lock (_lock)
                {
                    int n = 0;
                    for (int i = 0; i < _windows.Count; i++) if (_windows[i].IsOpen) n++;
                    return n;
                }
            }
        }

        /// <summary>累计打开窗口次数。</summary>
        public static long WindowOpenTotal { get { return Interlocked.Read(ref _windowOpenCount); } }

        /// <summary>
        /// 打开(或创建)一个窗口。同一 mod 内用 <paramref name="id"/> 复用。
        /// </summary>
        public static ModWindowInfo OpenWindow(string id, string title = null, ModContext owner = null)
        {
            if (string.IsNullOrEmpty(id)) return null;

            MainThreadGuard("OpenWindow");
            EnsureInstalled();

            var resolved = owner ?? SafeCurrentContext();
            string ownerId = resolved != null ? resolved.ModId : "SDK";
            string key = ownerId + "/" + id;

            ModWindowInfo window;
            lock (_lock)
            {
                window = _windows.Find(w => string.Equals(w.Id, key, StringComparison.Ordinal));
                if (window == null)
                {
                    window = new ModWindowInfo
                    {
                        Id = key,
                        Title = title ?? id,
                        Owner = new ModContextRef(resolved),
                        Order = ++_windowOrder,
                        Width = 320f,
                        Height = 200f
                    };
                    _windows.Add(window);
                }
                else if (!string.IsNullOrEmpty(title))
                {
                    window.Title = title;
                }

                if (!window.IsOpen)
                {
                    window.IsOpen = true;
                    window.Order = ++_windowOrder;
                    Interlocked.Increment(ref _windowOpenCount);
                }
            }

            if (resolved != null) resolved.RegisterCleanup(() => CloseWindowByKey(key));
            try { UIEvents.RaiseWindowOpened(window); } catch { }
            return window;
        }

        /// <summary>关闭窗口(按 id; 需要 owner 时的完整键为 "ModId/id")。</summary>
        public static bool CloseWindow(string id, ModContext owner = null)
        {
            if (string.IsNullOrEmpty(id)) return false;

            var resolved = owner ?? SafeCurrentContext();
            string key = id;
            if (owner != null || id.IndexOf('/') < 0)
            {
                string ownerId = resolved != null ? resolved.ModId : "SDK";
                if (id.IndexOf('/') < 0) key = ownerId + "/" + id;
            }

            lock (_lock)
            {
                var window = _windows.Find(w => string.Equals(w.Id, key, StringComparison.Ordinal));
                if (window == null || !window.IsOpen) return false;
                window.IsOpen = false;
            }

            try { UIEvents.RaiseWindowClosed(key); } catch { }
            return true;
        }

        /// <summary>窗口是否打开。</summary>
        public static bool IsWindowOpen(string id, ModContext owner = null)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var resolved = owner ?? SafeCurrentContext();
            string ownerId = resolved != null ? resolved.ModId : "SDK";
            string key = id.IndexOf('/') >= 0 ? id : ownerId + "/" + id;

            lock (_lock)
            {
                var window = _windows.Find(w => string.Equals(w.Id, key, StringComparison.Ordinal));
                return window != null && window.IsOpen;
            }
        }

        /// <summary>全部窗口(只读快照)。</summary>
        public static IList<ModWindowInfo> Windows
        {
            get { lock (_lock) { return _windows.ToArray(); } }
        }

        /// <summary>移除某个 mod 的全部窗口。</summary>
        public static int RemoveWindows(ModContext owner)
        {
            if (owner == null) return 0;
            lock (_lock) { return _windows.RemoveAll(w => w.Owner != null && w.Owner.ModId == owner.ModId); }
        }

        private static void CloseWindowByKey(string key)
        {
            lock (_lock)
            {
                var window = _windows.Find(w => string.Equals(w.Id, key, StringComparison.Ordinal));
                if (window != null) window.IsOpen = false;
            }
        }

        // =====================================================================
        // 覆盖层
        // =====================================================================

        /// <summary>覆盖层总数。</summary>
        public static int OverlayCount { get { lock (_lock) { return _overlays.Count; } } }

        /// <summary>可见覆盖层数。</summary>
        public static int VisibleOverlayCount
        {
            get
            {
                lock (_lock)
                {
                    int n = 0;
                    for (int i = 0; i < _overlays.Count; i++) if (_overlays[i].IsVisible) n++;
                    return n;
                }
            }
        }

        /// <summary>注册(或取出)一个覆盖层。默认可见。</summary>
        public static ModOverlayInfo RegisterOverlay(string id, bool visible = true, ModContext owner = null)
        {
            if (string.IsNullOrEmpty(id)) return null;

            MainThreadGuard("RegisterOverlay");
            EnsureInstalled();

            var resolved = owner ?? SafeCurrentContext();
            string ownerId = resolved != null ? resolved.ModId : "SDK";
            string key = ownerId + "/" + id;

            ModOverlayInfo overlay;
            lock (_lock)
            {
                overlay = _overlays.Find(o => string.Equals(o.Id, key, StringComparison.Ordinal));
                if (overlay == null)
                {
                    overlay = new ModOverlayInfo
                    {
                        Id = key,
                        Owner = new ModContextRef(resolved),
                        Order = ++_windowOrder,
                        IsVisible = visible
                    };
                    _overlays.Add(overlay);
                }
                else
                {
                    overlay.IsVisible = visible;
                }
            }

            if (resolved != null) resolved.RegisterCleanup(() => RemoveOverlayByKey(key));
            return overlay;
        }

        /// <summary>设置覆盖层可见性。</summary>
        public static bool SetOverlayVisible(string id, bool visible, ModContext owner = null)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var resolved = owner ?? SafeCurrentContext();
            string ownerId = resolved != null ? resolved.ModId : "SDK";
            string key = id.IndexOf('/') >= 0 ? id : ownerId + "/" + id;

            lock (_lock)
            {
                var overlay = _overlays.Find(o => string.Equals(o.Id, key, StringComparison.Ordinal));
                if (overlay == null) return false;
                overlay.IsVisible = visible;
                return true;
            }
        }

        /// <summary>全部覆盖层(只读快照)。</summary>
        public static IList<ModOverlayInfo> Overlays
        {
            get { lock (_lock) { return _overlays.ToArray(); } }
        }

        /// <summary>移除某个 mod 的全部覆盖层。</summary>
        public static int RemoveOverlays(ModContext owner)
        {
            if (owner == null) return 0;
            lock (_lock) { return _overlays.RemoveAll(o => o.Owner != null && o.Owner.ModId == owner.ModId); }
        }

        private static void RemoveOverlayByKey(string key)
        {
            lock (_lock) { _overlays.RemoveAll(o => string.Equals(o.Id, key, StringComparison.Ordinal)); }
        }

        // =====================================================================
        // GUI 回调
        // =====================================================================

        /// <summary>已登记的 GUI 回调数。</summary>
        public static int GuiCallbackCount { get { return Volatile.Read(ref _guiSnapshot).Length; } }

        /// <summary>
        /// 登记每帧 GUI 绘制回调(由 mod 的 <c>OnGUI</c> 覆写自动调用, 也可手动登记)。
        /// 只有安装了渲染后端时才会被真正调用。
        /// </summary>
        public static void RegisterGuiCallback(Action onGui, ModContext owner = null, string tag = null)
        {
            if (onGui == null) return;

            var resolved = owner ?? SafeCurrentContext();
            long id;

            lock (_lock)
            {
                // 去重
                foreach (var entry in _guiCallbacks)
                {
                    if (ReferenceEquals(entry.Callback, onGui) && entry.Owner == resolved) return;
                }

                id = ++_nextNotificationId;
                _guiCallbacks.Add(new GuiEntry { Id = id, Callback = onGui, Owner = resolved, Tag = tag });
                RebuildGuiSnapshotLocked();
            }

            EnsureInstalled();

            if (resolved != null) resolved.RegisterCleanup(() => RemoveGuiCallbackById(id));
        }

        /// <summary>按委托移除 GUI 回调。</summary>
        public static bool UnregisterGuiCallback(Action onGui)
        {
            if (onGui == null) return false;
            lock (_lock)
            {
                int removed = _guiCallbacks.RemoveAll(e => ReferenceEquals(e.Callback, onGui));
                if (removed > 0) RebuildGuiSnapshotLocked();
                return removed > 0;
            }
        }

        /// <summary>移除某个 mod 的全部 UI 登记(窗口/覆盖层/通知/GUI 回调)。</summary>
        public static void RemoveAllForMod(ModContext owner)
        {
            if (owner == null) return;
            RemoveWindows(owner);
            RemoveOverlays(owner);
            ClearNotifications(owner);

            lock (_lock)
            {
                _guiCallbacks.RemoveAll(e => e.Owner == owner);
                RebuildGuiSnapshotLocked();
            }
        }

        private static void RemoveGuiCallbackById(long id)
        {
            lock (_lock)
            {
                _guiCallbacks.RemoveAll(e => e.Id == id);
                RebuildGuiSnapshotLocked();
            }
        }

        private static void RebuildGuiSnapshotLocked()
        {
            var array = new Action[_guiCallbacks.Count];
            for (int i = 0; i < _guiCallbacks.Count; i++) array[i] = _guiCallbacks[i].Callback;
            Volatile.Write(ref _guiSnapshot, array);
        }

        /// <summary>
        /// 由 UI 后端在 GUI 绘制时机调用: 依次执行所有登记的 GUI 回调。
        /// </summary>
        public static void InvokeGuiCallbacks()
        {
            var snapshot = Volatile.Read(ref _guiSnapshot);
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i](); }
                catch (Exception e) { SdkLog.ReportCrash("UI", "GUI 回调", e); }
            }
        }

        // =====================================================================
        // 状态
        // =====================================================================

        /// <summary>是否有 mod UI 处于打开状态(窗口打开 / 覆盖层可见 / 有待显示通知)。</summary>
        public static bool IsAnyModUiOpen()
        {
            lock (_lock)
            {
                for (int i = 0; i < _windows.Count; i++) if (_windows[i].IsOpen) return true;
                for (int i = 0; i < _overlays.Count; i++) if (_overlays[i].IsVisible) return true;
                return _notifications.Count > 0;
            }
        }

        /// <summary>
        /// 是否应该屏蔽游戏输入(自由相机等应据此暂停响应)。
        ///
        /// 只统计"需要输入独占的 mod 窗口/悬浮层", <b>不包含临时通知</b>。
        /// 通知(Notify)只是几秒的提示条, 不该挡住输入: 否则"自由相机: 开启"这条通知
        /// 会在它的 TTL 内把自由相机自己的输入整个屏蔽掉(游戏内实测: 按 F1 后 3 秒内
        /// 转视角完全无响应)。
        /// </summary>
        public static bool ShouldBlockGameInput
        {
            get { return IsAnyWindowOrOverlayOpen(); }
        }

        /// <summary>是否有 mod 窗口/悬浮层处于打开状态(不含通知)。</summary>
        public static bool IsAnyWindowOrOverlayOpen()
        {
            lock (_lock)
            {
                for (int i = 0; i < _windows.Count; i++) if (_windows[i].IsOpen) return true;
                for (int i = 0; i < _overlays.Count; i++) if (_overlays[i].IsVisible) return true;
                return false;
            }
        }

        /// <summary>UI 状态快照(诊断用)。</summary>
        public static UiState Describe()
        {
            var state = new UiState
            {
                RenderingAvailable = IsRenderingAvailable,
                BackendName = _backend != null ? _backend.Name : null,
                NotificationCount = NotificationCount,
                WindowCount = WindowCount,
                OpenWindowCount = OpenWindowCount,
                OverlayCount = OverlayCount,
                VisibleOverlayCount = VisibleOverlayCount,
                GuiCallbackCount = GuiCallbackCount,
                AnyUiOpen = IsAnyModUiOpen()
            };

            lock (_lock)
            {
                for (int i = 0; i < _windows.Count; i++)
                {
                    if (_windows[i].IsOpen) state.OpenWindows.Add(_windows[i].ToString());
                }
            }

            return state;
        }

        /// <summary>清空全部 UI 状态(测试/关停用)。</summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                _notifications.Clear();
                _windows.Clear();
                _overlays.Clear();
                _guiCallbacks.Clear();
                RebuildGuiSnapshotLocked();
            }
        }

        // =====================================================================
        // 内部
        // =====================================================================

        private static void EnsureInstalled()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;
            try { UpdateService.SubscribeUpdate(Tick, null, "UiService.Tick"); }
            catch (Exception e) { SdkLog.Warn("UI", "注册 UI 每帧任务失败: " + e.Message); }
        }

        /// <summary>每帧: 通知过期 + 调用渲染后端。</summary>
        private static void Tick()
        {
            bool expired = false;
            try { expired = ExpireNotifications(); }
            catch (Exception e) { SdkLog.Error("UI", "通知过期处理失败: " + e.Message); }

            try { RenderTick(); }
            catch (Exception e) { SdkLog.Error("UI", "UI 渲染失败: " + e.Message); }

            // UI 开合状态变化 -> 通知订阅者(自由相机据此暂停输入)
            try
            {
                bool open = IsAnyModUiOpen();
                if (open != _lastUiOpenState || (expired && !open))
                {
                    _lastUiOpenState = open;
                    UIEvents.RaiseUiOpenStateChanged(open);
                }
            }
            catch { }
        }

        private static bool _lastUiOpenState;

        private static bool ExpireNotifications()
        {
            float now = Now();
            bool removedAny = false;
            lock (_lock)
            {
                for (int i = _notifications.Count - 1; i >= 0; i--)
                {
                    var notification = _notifications[i];
                    if (notification.Ttl <= 0f) continue;
                    if (now - notification.CreatedAt < notification.Ttl) continue;
                    _notifications.RemoveAt(i);
                    removedAny = true;
                    Interlocked.Increment(ref _expiredCount);
                }
            }
            return removedAny;
        }

        private static void RenderTick()
        {
            var backend = _backend;
            if (backend == null) return;

            ModNotification[] notifications;
            ModWindowInfo[] windows;
            ModOverlayInfo[] overlays;

            lock (_lock)
            {
                notifications = _notifications.ToArray();
                windows = _windows.ToArray();
                overlays = _overlays.ToArray();
            }

            if (notifications.Length > 0) backend.DrawNotifications(notifications);
            if (windows.Length > 0) backend.DrawWindows(windows);
            if (overlays.Length > 0) backend.DrawOverlays(overlays);
        }

        /// <summary>已过期通知累计数。</summary>
        public static long ExpiredNotificationCount { get { return Interlocked.Read(ref _expiredCount); } }

        private static float Now()
        {
            return UnityCall.RealtimeSinceStartup();
        }

        private static void MainThreadGuard(string what)
        {
            MainThread.AssertMainThread("UiService." + what);
        }

        private static ModContext SafeCurrentContext()
        {
            try { return ModContext.Current; }
            catch { return null; }
        }
    }
}
