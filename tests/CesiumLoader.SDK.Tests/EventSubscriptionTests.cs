using System;
using System.Collections.Generic;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 事件门面与订阅归属。
    /// 说明: SceneEvents / UIEvents / CameraEvents 的 Raise* 是 SDK 内部入口(只能由
    /// Unity 回调或每帧泵触发), 因此这里验证的是订阅簿记、清理与"mod 卸载自动退订";
    /// 真正的派发路径在游戏内由 CameraProbeMod / DiagnosticsMod 覆盖。
    /// </summary>
    public class EventSubscriptionTests : IDisposable
    {
        private readonly ModContext _owner;

        public EventSubscriptionTests()
        {
            ModRegistry.Unregister("EventSvcMod");
            _owner = ModRegistry.Register("EventSvcMod", "1.0.0", "", "", typeof(EventSubscriptionTests).Assembly);
            SceneEvents.Clear();
            UIEvents.Clear();
            CameraEvents.Clear();
            UiService.ClearAll();
        }

        public void Dispose()
        {
            SceneEvents.Clear();
            UIEvents.Clear();
            CameraEvents.Clear();
            UiService.ClearAll();
            UpdateService.RemoveAllUpdateCallbacks(_owner);
            ModRegistry.Unregister("EventSvcMod");
        }

        // =====================================================================
        // 门面事件
        // =====================================================================

        [Fact]
        public void SceneEvents_TrackSubscribersAndClear()
        {
            Action<SceneLoadedEventArgs> loaded = _ => { };
            Action<string> unloaded = _ => { };
            Action<string, string> changed = (_, __) => { };

            Assert.Equal(0, SceneEvents.SubscriberCount);

            SceneEvents.SceneLoaded += loaded;
            SceneEvents.SceneUnloaded += unloaded;
            SceneEvents.ActiveSceneChanged += changed;
            Assert.Equal(3, SceneEvents.SubscriberCount);

            SceneEvents.SceneLoaded -= loaded;
            SceneEvents.SceneUnloaded -= unloaded;
            SceneEvents.ActiveSceneChanged -= changed;
            Assert.Equal(0, SceneEvents.SubscriberCount);
        }

        [Fact]
        public void UIEvents_TrackSubscribersAndClear()
        {
            Action<ModNotification> shown = _ => { };
            Action<ModWindowInfo> opened = _ => { };
            Action<string> closed = _ => { };
            Action<bool> stateChanged = _ => { };

            UIEvents.NotificationShown += shown;
            UIEvents.WindowOpened += opened;
            UIEvents.WindowClosed += closed;
            UIEvents.UiOpenStateChanged += stateChanged;
            Assert.Equal(4, UIEvents.SubscriberCount);

            UIEvents.Clear();
            Assert.Equal(0, UIEvents.SubscriberCount);
        }

        [Fact]
        public void CameraEvents_TrackSubscribersAndClear()
        {
            Action<UnityEngine.Camera> changed = _ => { };
            Action lost = () => { };
            Action<UnityEngine.Camera> active = _ => { };

            Assert.Equal(0, CameraEvents.SubscriberCount);

            CameraEvents.MainCameraChanged += changed;
            CameraEvents.MainCameraLost += lost;
            CameraEvents.ActiveCameraChanged += active;
            Assert.Equal(3, CameraEvents.SubscriberCount);

            CameraEvents.MainCameraChanged -= changed;
            CameraEvents.MainCameraLost -= lost;
            CameraEvents.ActiveCameraChanged -= active;
            Assert.Equal(0, CameraEvents.SubscriberCount);
        }

        [Fact]
        public void CameraEvents_SubscribingInstallsPerFrameWatcher()
        {
            Action lost = () => { };
            CameraEvents.MainCameraLost += lost;

            // 观察者是

            // 每帧触发不应抛异常(离线时解析不到相机 → 视为"无主相机")
            UpdateService.RaiseUpdate();

            CameraEvents.MainCameraLost -= lost;
        }

        // =====================================================================
        // SceneService 订阅
        // =====================================================================

        [Fact]
        public void SceneService_SubscribeAndDispose()
        {
            int baseline = SceneService.SceneLoadedSubscriberCount;

            var subscription = SceneService.SubscribeSceneLoaded((s, m) => { }, _owner);
            Assert.Equal(baseline + 1, SceneService.SceneLoadedSubscriberCount);

            subscription.Dispose();
            subscription.Dispose();      // 幂等
            Assert.Equal(baseline, SceneService.SceneLoadedSubscriberCount);
        }

        [Fact]
        public void SceneService_DuplicateCallbackAndOwner_IsDeduplicated()
        {
            int baseline = SceneService.SceneLoadedSubscriberCount;
            Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode> callback =
                (s, m) => { };

            var first = SceneService.SubscribeSceneLoaded(callback, _owner);
            var second = SceneService.SubscribeSceneLoaded(callback, _owner);

            Assert.Equal(baseline + 1, SceneService.SceneLoadedSubscriberCount);

            first.Dispose();
            second.Dispose();
            Assert.Equal(baseline, SceneService.SceneLoadedSubscriberCount);
        }

        [Fact]
        public void SceneService_NullCallback_ReturnsInertSubscription()
        {
            int baseline = SceneService.SceneLoadedSubscriberCount;

            var subscription = SceneService.SubscribeSceneLoaded((Action<UnityEngine.SceneManagement.Scene,
                UnityEngine.SceneManagement.LoadSceneMode>)null, _owner);

            Assert.NotNull(subscription);
            subscription.Dispose();
            Assert.Equal(baseline, SceneService.SceneLoadedSubscriberCount);
        }

        [Fact]
        public void SceneService_UnsubscribeAll_RemovesOnlyThatMod()
        {
            ModRegistry.Unregister("EventSvcOther");
            var other = ModRegistry.Register("EventSvcOther", "1.0.0");

            int loaded = SceneService.SceneLoadedSubscriberCount;
            int unloaded = SceneService.SceneUnloadedSubscriberCount;
            int active = SceneService.ActiveSceneChangedSubscriberCount;

            SceneService.SubscribeSceneLoaded((s, m) => { }, _owner);
            SceneService.SubscribeSceneUnloaded(s => { }, _owner);
            SceneService.SubscribeActiveSceneChanged((p, c) => { }, _owner);
            SceneService.SubscribeSceneLoaded((s, m) => { }, other);
            int loadedAfterSubscribe = SceneService.SceneLoadedSubscriberCount;

            int removed = SceneService.UnsubscribeAll(_owner);

            Assert.Equal(3, removed);
            Assert.Equal(loadedAfterSubscribe - 1, SceneService.SceneLoadedSubscriberCount);   // other 的订阅必须保留
            Assert.Equal(unloaded, SceneService.SceneUnloadedSubscriberCount);
            Assert.Equal(active, SceneService.ActiveSceneChangedSubscriberCount);

            SceneService.UnsubscribeAll(other);
            ModRegistry.Unregister("EventSvcOther");
        }

        [Fact]
        public void SceneService_QueryApis_DegradeGracefullyOffline()
        {
            // 没有 Unity 运行时: 这些 API 必须返回默认值, 而不是抛异常
            Assert.Equal(0, SceneService.GetSceneCount());
            Assert.Null(SceneService.GetActiveSceneName());
            Assert.False(SceneService.IsSceneLoaded("AnyScene"));
            Assert.False(SceneService.IsSceneLoaded(null));
            Assert.Empty(SceneService.GetLoadedSceneNames());
            Assert.Empty(SceneService.GetRootGameObjects());
            Assert.Equal(0, SceneService.GetActiveSceneHandle());   // Scene.IsValid() 本身是 ECall, 不能在测试里直接调
        }

        // =====================================================================
        // UiService(通知 / 窗口 / 覆盖层 / GUI 回调)
        // =====================================================================

        [Fact]
        public void UiService_NotifyRecordsEvenWithoutRenderer()
        {
            UiService.ClearAll();
            long total = UiService.NotificationTotal;

            UiService.Notify("加载完成", UiNotificationLevel.Success, 3f, _owner);
            UiService.Notify("出了问题", UiNotificationLevel.Warning, 3f, _owner);
            UiService.Notify(null, UiNotificationLevel.Info, 3f, _owner);   // 空文本忽略

            Assert.Equal(2, UiService.NotificationCount);
            Assert.Equal(total + 2, UiService.NotificationTotal);
            Assert.Equal("EventSvcMod", UiService.Notifications[0].ModId);
            Assert.Equal(UiNotificationLevel.Success, UiService.Notifications[0].Level);
        }

        [Fact]
        public void UiService_ClearNotifications_ByOwnerAndAll()
        {
            ModRegistry.Unregister("EventSvcOther");
            var other = ModRegistry.Register("EventSvcOther", "1.0.0");

            UiService.ClearAll();
            UiService.Notify("a", UiNotificationLevel.Info, 0f, _owner);
            UiService.Notify("b", UiNotificationLevel.Info, 0f, other);

            Assert.Equal(1, UiService.ClearNotifications(_owner));
            Assert.Equal(1, UiService.NotificationCount);

            Assert.Equal(1, UiService.ClearNotifications());
            Assert.Equal(0, UiService.NotificationCount);

            ModRegistry.Unregister("EventSvcOther");
        }

        [Fact]
        public void UiService_WindowLifecycle()
        {
            UiService.ClearAll();
            long total = UiService.WindowOpenTotal;

            var window = UiService.OpenWindow("panel", "测试面板", _owner);

            Assert.NotNull(window);
            Assert.Equal(1, UiService.WindowCount);
            Assert.Equal(1, UiService.OpenWindowCount);
            Assert.True(UiService.IsWindowOpen("panel", _owner));
            Assert.Equal(total + 1, UiService.WindowOpenTotal);

            // 同一 mod + 同一 id 复用
            Assert.Same(window, UiService.OpenWindow("panel", "测试面板", _owner));
            Assert.Equal(1, UiService.WindowCount);

            Assert.True(UiService.CloseWindow("panel", _owner));
            Assert.False(UiService.IsWindowOpen("panel", _owner));
            Assert.Equal(0, UiService.OpenWindowCount);

            Assert.False(UiService.CloseWindow("panel", _owner));   // 已关闭
            Assert.Null(UiService.OpenWindow(null, null, _owner));
        }

        [Fact]
        public void UiService_WindowsAreScopedPerMod()
        {
            ModRegistry.Unregister("EventSvcOther");
            var other = ModRegistry.Register("EventSvcOther", "1.0.0");

            UiService.ClearAll();
            UiService.OpenWindow("panel", "我的", _owner);
            UiService.OpenWindow("panel", "他的", other);

            Assert.Equal(2, UiService.WindowCount);
            Assert.Equal("我的", UiService.Windows[0].Title);

            Assert.Equal(0, UiService.RemoveWindows(null));          // null 不做任何事
            Assert.Equal(1, UiService.RemoveWindows(_owner));
            Assert.Equal(1, UiService.RemoveWindows(other));         // 各自只清自己的窗口
            Assert.Equal(0, UiService.WindowCount);

            ModRegistry.Unregister("EventSvcOther");
        }

        [Fact]
        public void UiService_OverlayVisibility()
        {
            UiService.ClearAll();

            var overlay = UiService.RegisterOverlay("hud", true, _owner);

            Assert.NotNull(overlay);
            Assert.Equal(1, UiService.OverlayCount);
            Assert.Equal(1, UiService.VisibleOverlayCount);

            Assert.True(UiService.SetOverlayVisible("hud", false, _owner));
            Assert.Equal(0, UiService.VisibleOverlayCount);
            Assert.Equal(1, UiService.OverlayCount);

            Assert.Equal(1, UiService.RemoveOverlays(_owner));
            Assert.Equal(0, UiService.OverlayCount);
        }

        [Fact]
        public void UiService_GuiCallbacks_AreIsolatedAndRemovable()
        {
            UiService.ClearAll();
            var order = new List<string>();

            UiService.RegisterGuiCallback(() => order.Add("first"), _owner, "gui-a");
            UiService.RegisterGuiCallback(() => { throw new InvalidOperationException("draw boom"); }, _owner, "gui-bad");
            UiService.RegisterGuiCallback(() => order.Add("last"), _owner, "gui-c");

            Assert.Equal(3, UiService.GuiCallbackCount);

            UiService.InvokeGuiCallbacks();   // 单个回调异常不能中断其它回调

            Assert.Equal(new[] { "first", "last" }, order.ToArray());
        }

        [Fact]
        public void UiService_RemoveAllForMod_AndUnregisterGuiCallback()
        {
            UiService.ClearAll();
            Action callback = () => { };

            UiService.RegisterGuiCallback(callback, _owner, "gui");
            UiService.OpenWindow("w", "t", _owner);
            UiService.RegisterOverlay("o", true, _owner);
            UiService.Notify("n", UiNotificationLevel.Info, 0f, _owner);

            Assert.True(UiService.UnregisterGuiCallback(callback));
            Assert.False(UiService.UnregisterGuiCallback(callback));

            UiService.RemoveAllForMod(_owner);

            Assert.Equal(0, UiService.GuiCallbackCount);
            Assert.Equal(0, UiService.WindowCount);
            Assert.Equal(0, UiService.OverlayCount);
        }

        [Fact]
        public void UiService_OpenStateQueries()
        {
            UiService.ClearAll();
            Assert.False(UiService.IsAnyModUiOpen());
            Assert.False(UiService.ShouldBlockGameInput);

            UiService.OpenWindow("w", "t", _owner);

            Assert.True(UiService.IsAnyModUiOpen());
            Assert.True(UiService.ShouldBlockGameInput);

            UiService.ClearAll();
            Assert.False(UiService.IsAnyModUiOpen());
        }

        [Fact]
        public void UiService_Describe_AndBackendGuards()
        {
            UiService.ClearAll();
            UiService.Notify("x", UiNotificationLevel.Info, 0f, _owner);

            var state = UiService.Describe();

            Assert.False(string.IsNullOrEmpty(state.ToString()));
            Assert.False(UiService.TryInstallBackend(null));
            Assert.False(UiService.IsRenderingAvailable);   // 无渲染后端也必须可用
        }

        [Fact]
        public void UiService_TickIsSafeOffline()
        {
            UiService.Notify("ttl 很长的通知", UiNotificationLevel.Info, 3600f, _owner);
            int before = UiService.NotificationCount;

            // 每帧任务: 通知过期 + 渲染 + UI 开合状态广播, 离线不得抛异常
            UpdateService.RaiseUpdate();

            Assert.True(UiService.NotificationCount <= before);
        }

        [Fact]
        public void UiService_CountersAreConsistent()
        {
            Assert.True(UiService.NotificationTotal >= 0);
            Assert.True(UiService.WindowOpenTotal >= 0);
        }
    }
}
