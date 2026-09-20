using System;
using System.Threading;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 相机事件(门面形式): 主相机出现/消失/更换、活动相机更换。
    ///
    /// 为什么需要: Unity 没有"主相机变更"事件, 而切场景/换相机时该引用会失效。
    /// 这里用一个每帧观察者(只在有人订阅时才安装)做变更检测, 让 mod 能在相机
    /// 被销毁后自动重新绑定 —— 不需要自己缓存一个可能失效的相机引用。
    ///
    /// 所有回调都在主线程。
    /// </summary>
    public static class CameraEvents
    {
        private static readonly object _lock = new object();
        private static int _installed;
        private static int _lastMainCameraId;
        private static int _lastActiveCameraId;

        private static Action<Camera> _mainCameraChanged;
        private static Action _mainCameraLost;
        private static Action<Camera> _activeCameraChanged;

        /// <summary>主相机出现或更换(参数为新的主相机)。</summary>
        public static event Action<Camera> MainCameraChanged
        {
            add { EnsureWatcher(); lock (_lock) { _mainCameraChanged += value; } }
            remove { lock (_lock) { _mainCameraChanged -= value; } }
        }

        /// <summary>主相机消失(被销毁或不再存在)。</summary>
        public static event Action MainCameraLost
        {
            add { EnsureWatcher(); lock (_lock) { _mainCameraLost += value; } }
            remove { lock (_lock) { _mainCameraLost -= value; } }
        }

        /// <summary>SDK 认定的活动相机更换。</summary>
        public static event Action<Camera> ActiveCameraChanged
        {
            add { EnsureWatcher(); lock (_lock) { _activeCameraChanged += value; } }
            remove { lock (_lock) { _activeCameraChanged -= value; } }
        }

        /// <summary>事件总数(诊断用)。</summary>
        public static int SubscriberCount
        {
            get
            {
                lock (_lock)
                {
                    int n = 0;
                    if (_mainCameraChanged != null) n += _mainCameraChanged.GetInvocationList().Length;
                    if (_mainCameraLost != null) n += _mainCameraLost.GetInvocationList().Length;
                    if (_activeCameraChanged != null) n += _activeCameraChanged.GetInvocationList().Length;
                    return n;
                }
            }
        }

        /// <summary>检测到的相机变更次数。</summary>
        public static long ChangeCount { get { return Interlocked.Read(ref _changes); } }
        private static long _changes;

        /// <summary>清空全部订阅。</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _mainCameraChanged = null;
                _mainCameraLost = null;
                _activeCameraChanged = null;
            }
        }

        /// <summary>每帧相机观察者是否已安装(诊断/测试用)。安装是一次性的, 不会随订阅清空而卸载。</summary>
        public static bool WatcherInstalled { get { return Volatile.Read(ref _installed) != 0; } }

        private static void EnsureWatcher()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;

            try { UpdateService.SubscribeUpdate(Tick, null, "CameraEvents.Tick"); }
            catch (Exception e) { SdkLog.Warn("CAMERA", "注册相机观察者失败: " + e.Message); }
        }

        private static void Tick()
        {
            try
            {
                var main = CameraService.GetMainCamera();
                int mainId = UnityObject.IsAlive(main) ? UnityObject.GetInstanceId(main) : 0;

                if (mainId != _lastMainCameraId)
                {
                    Interlocked.Increment(ref _changes);
                    _lastMainCameraId = mainId;

                    if (mainId == 0)
                    {
                        RaiseMainCameraLost();
                    }
                    else
                    {
                        RaiseMainCameraChanged(main);
                    }
                }

                var active = CameraService.GetActiveCamera();
                int activeId = UnityObject.IsAlive(active) ? UnityObject.GetInstanceId(active) : 0;
                if (activeId != _lastActiveCameraId)
                {
                    _lastActiveCameraId = activeId;
                    RaiseActiveCameraChanged(active);
                }
            }
            catch (Exception e)
            {
                SdkLog.Error("CAMERA", "相机变更检测失败: " + e.Message);
            }
        }

        private static void RaiseMainCameraChanged(Camera camera)
        {
            Action<Camera> handler;
            lock (_lock) { handler = _mainCameraChanged; }
            if (handler == null) return;

            foreach (Action<Camera> d in handler.GetInvocationList())
            {
                try { d(camera); }
                catch (Exception e) { SdkLog.ReportCrash("CameraEvents", "MainCameraChanged", e); }
            }
        }

        private static void RaiseMainCameraLost()
        {
            Action handler;
            lock (_lock) { handler = _mainCameraLost; }
            if (handler == null) return;

            foreach (Action d in handler.GetInvocationList())
            {
                try { d(); }
                catch (Exception e) { SdkLog.ReportCrash("CameraEvents", "MainCameraLost", e); }
            }
        }

        private static void RaiseActiveCameraChanged(Camera camera)
        {
            Action<Camera> handler;
            lock (_lock) { handler = _activeCameraChanged; }
            if (handler == null) return;

            foreach (Action<Camera> d in handler.GetInvocationList())
            {
                try { d(camera); }
                catch (Exception e) { SdkLog.ReportCrash("CameraEvents", "ActiveCameraChanged", e); }
            }
        }
    }
}
