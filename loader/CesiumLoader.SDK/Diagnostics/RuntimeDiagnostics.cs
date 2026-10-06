using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumLoader.SDK.Cameras;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Internals;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Runtime;
using CesiumLoader.SDK.Scenes;
using CesiumLoader.SDK.Scheduling;
using CesiumLoader.SDK.UserInterface;

namespace CesiumLoader.SDK.Diagnostics
{
    /// <summary>采集运行时信息。</summary>
    public static class RuntimeDiagnostics
    {
        /// <summary>采集当前运行时快照。</summary>
        public static RuntimeDump Collect()
        {
            var dump = new RuntimeDump { CapturedUtc = DateTime.UtcNow.ToString("o") };

            try { dump.SdkVersion = SdkVersion.Current; } catch { }
            dump.UnityVersion = UnityCall.UnityVersion();
            dump.GameVersion = UnityCall.AppVersion();
            dump.ProductName = UnityCall.ProductName();
            dump.IsWindows = UnityCall.IsWindows();

            try { dump.AssemblyCount = RuntimeAssemblyService.GetAssemblies().Length; } catch { }
            try
            {
                var names = RuntimeAssemblyService.GetAssemblyNames();
                for (int i = 0; i < names.Count && i < 80; i++) dump.Assemblies.Add(names[i]);
            }
            catch { }

            try
            {
                dump.MainThreadId = MainThread.MainThreadId;
                dump.MainThreadPumpRunning = MainThread.IsPumpRunning;
                dump.FrameCount = MainThread.FrameCount;
                dump.MainThreadQueueLength = MainThread.PendingCount;
                dump.MainThreadExecuted = MainThread.ExecutedCount;
                dump.MainThreadFailed = MainThread.FailedCount;
            }
            catch { }

            try
            {
                dump.UpdateSubscribers = UpdateService.UpdateSubscriberCount;
                dump.LateUpdateSubscribers = UpdateService.LateUpdateSubscriberCount;
            }
            catch { }

            try { dump.ActiveCoroutines = CoroutineService.ActiveCount; } catch { }

            try
            {
                dump.SceneSubscribers = SceneService.SceneLoadedSubscriberCount +
                                        SceneService.SceneUnloadedSubscriberCount +
                                        SceneService.ActiveSceneChangedSubscriberCount;
            }
            catch { }

            try
            {
                dump.InputAvailable = InputService.IsAvailable;
                dump.InputBackend = InputService.BackendName;
                dump.KeyboardCapture = InputService.KeyboardCaptureOwner;
                dump.MouseCapture = InputService.MouseCaptureOwner;
            }
            catch { }

            try
            {
                dump.UiRenderingAvailable = UiService.IsRenderingAvailable;
                dump.UiOpen = UiService.IsAnyModUiOpen();
            }
            catch { }

            try { dump.CinemachineAvailable = CinemachineService.IsAvailable; } catch { }

            try
            {
                dump.Il2CppAvailable = Il2CppInteropService.IsInitialized();
                dump.Il2CppAttached = Il2CppInteropService.IsAttached();
                dump.Il2CppDetail = Il2CppInteropService.Describe();
            }
            catch { }

            try { dump.RuntimeAssemblyLoaded = RuntimeAssemblyService.IsAssemblyLoaded("AstralParty.Runtime"); } catch { }

            try
            {
                var mods = ModRegistry.All;
                dump.ModCount = mods.Count;
                for (int i = 0; i < mods.Count; i++) dump.Mods.Add(mods[i].ToString());
            }
            catch { }

            return dump;
        }
    }
}
