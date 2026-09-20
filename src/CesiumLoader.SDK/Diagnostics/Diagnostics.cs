using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
namespace CesiumLoader.SDK
{
    /// <summary>运行时诊断快照(可 JSON 序列化)。</summary>
    [Serializable]
    public sealed class RuntimeDump
    {
        /// <summary>生成时刻(UTC)。</summary>
        public string CapturedUtc;

        /// <summary>SDK 版本。</summary>
        public string SdkVersion;

        /// <summary>Unity 版本。</summary>
        public string UnityVersion;

        /// <summary>游戏版本。</summary>
        public string GameVersion;

        /// <summary>产品名。</summary>
        public string ProductName;

        /// <summary>是否 Windows。</summary>
        public bool IsWindows;

        /// <summary>已加载程序集数量。</summary>
        public int AssemblyCount;

        /// <summary>主线程 id(0 = 未确认)。</summary>
        public int MainThreadId;

        /// <summary>主线程泵是否运行。</summary>
        public bool MainThreadPumpRunning;

        /// <summary>已泵出的帧数。</summary>
        public int FrameCount;

        /// <summary>主线程队列积压。</summary>
        public int MainThreadQueueLength;

        /// <summary>主线程累计执行数。</summary>
        public long MainThreadExecuted;

        /// <summary>主线程累计失败数。</summary>
        public long MainThreadFailed;

        /// <summary>Update 订阅数。</summary>
        public int UpdateSubscribers;

        /// <summary>LateUpdate 订阅数。</summary>
        public int LateUpdateSubscribers;

        /// <summary>活跃协程数。</summary>
        public int ActiveCoroutines;

        /// <summary>场景订阅数。</summary>
        public int SceneSubscribers;

        /// <summary>输入后端描述。</summary>
        public string InputBackend;

        /// <summary>输入是否可用。</summary>
        public bool InputAvailable;

        /// <summary>键盘独占者。</summary>
        public string KeyboardCapture;

        /// <summary>鼠标独占者。</summary>
        public string MouseCapture;

        /// <summary>UI 是否具备渲染能力。</summary>
        public bool UiRenderingAvailable;

        /// <summary>是否有 mod UI 打开。</summary>
        public bool UiOpen;

        /// <summary>Cinemachine 是否可用。</summary>
        public bool CinemachineAvailable;

        /// <summary>IL2CPP 互操作层是否就绪(GameAssembly.dll 导出齐全)。</summary>
        public bool Il2CppAvailable;

        /// <summary>当前线程是否已挂接到 IL2CPP 域。</summary>
        public bool Il2CppAttached;

        /// <summary>IL2CPP 互操作单行描述(含 domain 指针与程序集数)。</summary>
        public string Il2CppDetail;

        /// <summary>HybridCLR 热更程序集(AstralParty.Runtime)是否已加载。</summary>
        public bool RuntimeAssemblyLoaded;

        /// <summary>已注册 mod 数量。</summary>
        public int ModCount;

        /// <summary>已注册 mod 列表。</summary>
        public List<string> Mods = new List<string>();

        /// <summary>已加载程序集名(截断到前 80 个)。</summary>
        public List<string> Assemblies = new List<string>();

        /// <summary>多行文本。</summary>
        public string ToText()
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("===== CesiumLoader RuntimeDump =====");
            sb.AppendLine("时间(UTC)      : " + CapturedUtc);
            sb.AppendLine("SDK 版本       : " + SdkVersion);
            sb.AppendLine("Unity 版本     : " + UnityVersion + "   游戏版本: " + GameVersion + "   产品: " + ProductName);
            sb.AppendLine("主线程         : id=" + MainThreadId + " 泵运行=" + MainThreadPumpRunning +
                          " 帧数=" + FrameCount + " 队列=" + MainThreadQueueLength +
                          " 执行=" + MainThreadExecuted + " 失败=" + MainThreadFailed);
            sb.AppendLine("程序集         : " + AssemblyCount + " 个已加载");
            sb.AppendLine("每帧回调       : Update=" + UpdateSubscribers + " LateUpdate=" + LateUpdateSubscribers);
            sb.AppendLine("协程/场景订阅  : 协程=" + ActiveCoroutines + " 场景=" + SceneSubscribers);
            sb.AppendLine("输入           : 可用=" + InputAvailable + " 后端=" + InputBackend);
            sb.AppendLine("输入独占       : 键盘=" + (KeyboardCapture ?? "(无)") + " 鼠标=" + (MouseCapture ?? "(无)"));
            sb.AppendLine("UI             : 渲染=" + UiRenderingAvailable + " 打开=" + UiOpen);
            sb.AppendLine("Cinemachine    : 可用=" + CinemachineAvailable);
            sb.AppendLine("IL2CPP         : 就绪=" + Il2CppAvailable + " 线程已挂接=" + Il2CppAttached);
            sb.AppendLine("IL2CPP 明细    : " + (Il2CppDetail ?? "(未采集)"));
            sb.AppendLine("HybridCLR      : AstralParty.Runtime 已加载=" + RuntimeAssemblyLoaded);
            sb.AppendLine("已注册 Mod     : " + ModCount + " 个");
            for (int i = 0; i < Mods.Count; i++) sb.AppendLine("  - " + Mods[i]);
            return sb.ToString();
        }

        /// <summary>JSON 文本。</summary>
        public string ToJson()
        {
            return CesiumJson.SerializePretty(this);
        }

        /// <summary>摘要。</summary>
        public override string ToString() { return ToText(); }
    }

    /// <summary>相机诊断快照。</summary>
    [Serializable]
    public sealed class CameraDump
    {
        /// <summary>生成时刻(UTC)。</summary>
        public string CapturedUtc;

        /// <summary>相机数量。</summary>
        public int CameraCount;

        /// <summary>是否存在主相机。</summary>
        public bool HasMainCamera;

        /// <summary>主相机名。</summary>
        public string MainCameraName;

        /// <summary>主相机层级路径。</summary>
        public string MainCameraPath;

        /// <summary>主相机状态。</summary>
        public CameraState MainCameraState;

        /// <summary>是否检测到 Cinemachine。</summary>
        public bool CinemachineAvailable;

        /// <summary>Cinemachine 是否已初始化。</summary>
        public bool CinemachineInitialized;

        /// <summary>接管中的 VirtualCamera。</summary>
        public string ActiveVirtualCamera;

        /// <summary>Brain 是否启用。</summary>
        public bool BrainEnabled;

        /// <summary>是否正在混合。</summary>
        public bool BrainBlending;

        /// <summary>每台相机的一行摘要。</summary>
        public List<string> Cameras = new List<string>();

        /// <summary>Cinemachine 明细文本。</summary>
        public string CinemachineDetail;

        /// <summary>多行文本。</summary>
        public string ToText()
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("===== CameraDump =====");
            sb.AppendLine("时间(UTC)      : " + CapturedUtc);
            sb.AppendLine("相机数量       : " + CameraCount + "   主相机: " + (HasMainCamera ? "有" : "无"));
            if (HasMainCamera)
            {
                sb.AppendLine("主相机         : " + (MainCameraPath ?? MainCameraName ?? "(未知)"));
                sb.AppendLine("主相机状态     : " + MainCameraState.Describe());
            }
            sb.AppendLine("Cinemachine    : 可用=" + CinemachineAvailable + " 初始化=" + CinemachineInitialized +
                          " Brain启用=" + BrainEnabled + " 混合中=" + BrainBlending);
            sb.AppendLine("接管 VirtualCam: " + (ActiveVirtualCamera ?? "(无)"));
            for (int i = 0; i < Cameras.Count; i++) sb.AppendLine("  - " + Cameras[i]);
            if (!string.IsNullOrEmpty(CinemachineDetail))
            {
                sb.AppendLine("Cinemachine 明细:");
                sb.AppendLine(CinemachineDetail);
            }
            return sb.ToString();
        }

        /// <summary>JSON 文本。</summary>
        public string ToJson() { return CesiumJson.SerializePretty(this); }

        /// <summary>摘要。</summary>
        public override string ToString() { return ToText(); }
    }

    /// <summary>场景诊断快照。</summary>
    [Serializable]
    public sealed class SceneDump
    {
        /// <summary>生成时刻(UTC)。</summary>
        public string CapturedUtc;

        /// <summary>已加载场景数。</summary>
        public int SceneCount;

        /// <summary>活动场景名。</summary>
        public string ActiveSceneName;

        /// <summary>活动场景句柄。</summary>
        public int ActiveSceneHandle;

        /// <summary>已加载场景名列表。</summary>
        public List<string> Scenes = new List<string>();

        /// <summary>活动场景根对象数。</summary>
        public int RootObjectCount;

        /// <summary>根对象名(截断到前 60 个)。</summary>
        public List<string> RootObjects = new List<string>();

        /// <summary>场景加载订阅数。</summary>
        public int SceneSubscribers;

        /// <summary>已转发场景加载次数。</summary>
        public long ForwardedLoads;

        /// <summary>已转发活动场景切换次数。</summary>
        public long ForwardedActiveChanges;

        /// <summary>多行文本。</summary>
        public string ToText()
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("===== SceneDump =====");
            sb.AppendLine("时间(UTC)      : " + CapturedUtc);
            sb.AppendLine("已加载场景     : " + SceneCount + " 个   活动: " + (ActiveSceneName ?? "(无)") +
                          " (handle=" + ActiveSceneHandle + ")");
            for (int i = 0; i < Scenes.Count; i++) sb.AppendLine("  - " + Scenes[i]);
            sb.AppendLine("根对象         : " + RootObjectCount + " 个");
            for (int i = 0; i < RootObjects.Count; i++) sb.AppendLine("  - " + RootObjects[i]);
            sb.AppendLine("订阅/转发      : 订阅=" + SceneSubscribers +
                          " 加载转发=" + ForwardedLoads + " 活动场景转发=" + ForwardedActiveChanges);
            return sb.ToString();
        }

        /// <summary>JSON 文本。</summary>
        public string ToJson() { return CesiumJson.SerializePretty(this); }

        /// <summary>摘要。</summary>
        public override string ToString() { return ToText(); }
    }

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

    /// <summary>采集相机信息。</summary>
    public static class CameraDiagnostics
    {
        /// <summary>采集相机快照。</summary>
        public static CameraDump Collect()
        {
            var dump = new CameraDump { CapturedUtc = DateTime.UtcNow.ToString("o") };

            try
            {
                var cameras = CameraService.GetAllCameras(true);
                dump.CameraCount = cameras.Length;

                var main = CameraService.GetMainCamera();
                dump.HasMainCamera = UnityObject.IsAlive(main);
                if (dump.HasMainCamera)
                {
                    dump.MainCameraName = UnityObject.GetName(main);
                    dump.MainCameraPath = UnityObject.GetHierarchyPath(main);
                    dump.MainCameraState = CameraService.CaptureState(main);
                }

                for (int i = 0; i < cameras.Length && i < 40; i++)
                {
                    dump.Cameras.Add(CameraService.DescribeCamera(cameras[i]));
                }
                if (cameras.Length > 40) dump.Cameras.Add("... 还有 " + (cameras.Length - 40) + " 台相机");
            }
            catch (Exception e)
            {
                dump.Cameras.Add("采集相机信息失败: " + e.Message);
            }

            try
            {
                dump.CinemachineAvailable = CinemachineService.IsAvailable;
                dump.CinemachineInitialized = CinemachineService.IsInitialized;

                var brain = CinemachineService.FindBrain();
                if (brain != null)
                {
                    dump.BrainEnabled = CinemachineService.IsBrainEnabled(brain);
                    dump.BrainBlending = CinemachineService.IsBlending(brain);
                    dump.ActiveVirtualCamera = CinemachineService.GetActiveVirtualCameraName(brain);
                }

                dump.CinemachineDetail = CinemachineService.Describe();
            }
            catch (Exception e)
            {
                dump.CinemachineDetail = "采集 Cinemachine 信息失败: " + e.Message;
            }

            return dump;
        }
    }

    /// <summary>采集场景信息。</summary>
    public static class SceneDiagnostics
    {
        /// <summary>采集场景快照。</summary>
        public static SceneDump Collect()
        {
            var dump = new SceneDump { CapturedUtc = DateTime.UtcNow.ToString("o") };

            try
            {
                dump.SceneCount = SceneService.GetSceneCount();
                dump.Scenes = SceneService.GetLoadedSceneNames();

                var active = SceneService.GetActiveScene();
                dump.ActiveSceneName = UnityCall.SceneName(active);
                dump.ActiveSceneHandle = UnityCall.SceneHandle(active);

                var roots = SceneService.GetRootGameObjects(active);
                dump.RootObjectCount = roots.Length;
                for (int i = 0; i < roots.Length && i < 60; i++)
                {
                    var go = roots[i];
                    if (go == null) continue;
                    dump.RootObjects.Add(UnityCall.Name(go) + (UnityCall.ActiveInHierarchy(go) ? "" : " (未激活)"));
                }
                if (roots.Length > 60) dump.RootObjects.Add("... 还有 " + (roots.Length - 60) + " 个");
            }
            catch (Exception e)
            {
                dump.RootObjects.Add("采集场景信息失败: " + e.Message);
            }

            try
            {
                dump.SceneSubscribers = SceneService.SceneLoadedSubscriberCount +
                                        SceneService.SceneUnloadedSubscriberCount +
                                        SceneService.ActiveSceneChangedSubscriberCount;
                dump.ForwardedLoads = SceneService.ForwardedLoadCount;
                dump.ForwardedActiveChanges = SceneService.ForwardedActiveChangeCount;
            }
            catch { }

            return dump;
        }

        private static string SafeString(Func<string> get)
        {
            try { return get(); } catch { return null; }
        }
    }

    /// <summary>
    /// 诊断入口。
    ///
    /// <code>
    /// using CesiumLoader.SDK;
    ///
    /// SdkDiagnostics.Dump();            // 全量: 运行时 + 相机 + 场景
    /// SdkDiagnostics.DumpCameraInfo();  // 只看相机
    /// SdkDiagnostics.DumpSceneInfo();   // 只看场景
    /// </code>
    ///
    /// 输出位置: <c>CESIUM_LOG_DIR\cesium-loader.log</c>(与 mod 的 activity-mod.log 同目录),
    /// 同时把摘要打进 activity-mod.log(会转发到控制台)。
    ///
    /// 性能: 只应手动触发(按键/命令)。内部使用 on-demand 的场景扫描,
    /// 不会每帧生成字符串。
    /// </summary>
    public static class SdkDiagnostics
    {
        private static readonly object _fileLock = new object();
        private static long _dumpCount;

        /// <summary>已执行的 Dump 次数。</summary>
        public static long DumpCount { get { return _dumpCount; } }

        /// <summary>诊断日志文件名。</summary>
        public const string DumpFileName = "cesium-loader.log";

        /// <summary>诊断日志完整路径(无法定位时返回文件名)。</summary>
        public static string GetDumpFilePath()
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("CESIUM_LOG_DIR");
                if (string.IsNullOrEmpty(dir))
                {
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AstralParty_ModLoader", "logs");
                }
                return Path.Combine(dir, DumpFileName);
            }
            catch { return DumpFileName; }
        }

        /// <summary>全量诊断转储(运行时 + 相机 + 场景)。返回写入的文件路径。</summary>
        public static string Dump(string title = null)
        {
            var sb = new StringBuilder(4096);

            try
            {
                sb.AppendLine();
                sb.AppendLine("############################################################");
                sb.AppendLine("# CesiumLoader SDK 诊断转储 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                if (!string.IsNullOrEmpty(title)) sb.AppendLine("# 标题: " + title);
                sb.AppendLine("############################################################");

                sb.Append(RuntimeDiagnostics.Collect().ToText());
                sb.Append(CameraDiagnostics.Collect().ToText());
                sb.Append(SceneDiagnostics.Collect().ToText());

                sb.AppendLine("===== UI =====");
                sb.AppendLine(UiService.Describe().ToString());

                sb.AppendLine("===== SDK 计数器 =====");
                sb.AppendLine("相机解析        : " + CameraService.ResolveCount + " 次, 缓存命中 " + CameraService.CacheHitCount);
                sb.AppendLine("类型查找        : " + RuntimeAssemblyService.TypeLookupCount +
                              " 次, 命中 " + RuntimeAssemblyService.TypeCacheHits +
                              ", 未找到 " + RuntimeAssemblyService.TypeMissCount);
                sb.AppendLine("Cinemachine     : 解析尝试 " + CinemachineService.ResolveAttemptCount +
                              ", 创建 vcam " + CinemachineService.CreatedVirtualCameraCount +
                              ", 失败 " + CinemachineService.FailureCount);
                sb.AppendLine("输入查询        : " + InputService.QueryCount +
                              ", 独占授予 " + InputService.CaptureGrantedCount +
                              ", 拒绝 " + InputService.CaptureDeniedCount);
                sb.AppendLine("协程            : 启动 " + CoroutineService.StartedCount +
                              ", 完成 " + CoroutineService.CompletedCount +
                              ", 异常 " + CoroutineService.ExceptionCount);
                sb.AppendLine("每帧回调异常    : " + UpdateService.ExceptionCount);
                sb.AppendLine("主线程          : 入队 " + MainThread.PostedCount +
                              ", 执行 " + MainThread.ExecutedCount +
                              ", 失败 " + MainThread.FailedCount);
                sb.AppendLine("############################################################");
            }
            catch (Exception e)
            {
                sb.AppendLine("诊断采集异常: " + e);
            }

            string text = sb.ToString();
            string path = WriteToFile(text);

            _dumpCount++;
            SdkLog.Info("DIAG", "诊断已转储 -> " + path);
            SdkLog.Info("DIAG", "摘要: 主线程=" + MainThread.MainThreadId +
                               " 帧=" + MainThread.FrameCount +
                               " 相机=" + (CameraService.HasMainCamera ? "有" : "无") +
                               " 场景=" + (SafeActiveSceneName() ?? "?") +
                               " Cinemachine=" + (CinemachineService.IsAvailable ? "可用" : "不可用"));

            return path;
        }

        /// <summary>只转储相机信息。</summary>
        public static string DumpCameraInfo()
        {
            var dump = CameraDiagnostics.Collect();
            string path = WriteToFile(dump.ToText());
            SdkLog.Info("DIAG", "相机信息已转储 -> " + path);
            return path;
        }

        /// <summary>只转储场景信息。</summary>
        public static string DumpSceneInfo()
        {
            var dump = SceneDiagnostics.Collect();
            string path = WriteToFile(dump.ToText());
            SdkLog.Info("DIAG", "场景信息已转储 -> " + path);
            return path;
        }

        /// <summary>只转储运行时信息。</summary>
        public static string DumpRuntimeInfo()
        {
            var dump = RuntimeDiagnostics.Collect();
            string path = WriteToFile(dump.ToText());
            SdkLog.Info("DIAG", "运行时信息已转储 -> " + path);
            return path;
        }

        /// <summary>把 JSON 形式的完整诊断写到文件(给工具读取)。</summary>
        public static string DumpJson(string path = null)
        {
            try
            {
                var bundle = new DiagnosticBundle
                {
                    CapturedUtc = DateTime.UtcNow.ToString("o"),
                    Runtime = RuntimeDiagnostics.Collect(),
                    Camera = CameraDiagnostics.Collect(),
                    Scene = SceneDiagnostics.Collect(),
                    Ui = UiService.Describe()
                };

                string target = string.IsNullOrEmpty(path) ? GetDumpFilePath() + ".json" : path;
                WriteText(target, CesiumJson.SerializePretty(bundle));
                SdkLog.Info("DIAG", "JSON 诊断已转储 -> " + target);
                return target;
            }
            catch (Exception e)
            {
                SdkLog.Error("DIAG", "JSON 诊断失败: " + e.Message);
                return null;
            }
        }

        /// <summary>诊断包(JSON 根)。</summary>
        [Serializable]
        public sealed class DiagnosticBundle
        {
            /// <summary>生成时刻。</summary>
            public string CapturedUtc;

            /// <summary>运行时信息。</summary>
            public RuntimeDump Runtime;

            /// <summary>相机信息。</summary>
            public CameraDump Camera;

            /// <summary>场景信息。</summary>
            public SceneDump Scene;

            /// <summary>UI 状态。</summary>
            public UiState Ui;
        }

        private static string WriteToFile(string text)
        {
            string path = GetDumpFilePath();
            WriteText(path, text);
            return path;
        }

        private static void WriteText(string path, string text)
        {
            try
            {
                lock (_fileLock)
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    File.AppendAllText(path, text + "\r\n");
                }
            }
            catch (Exception e)
            {
                SdkLog.Error("DIAG", "写诊断文件失败: " + e.Message);
            }
        }

        private static string SafeActiveSceneName()
        {
            try { return SceneService.GetActiveSceneName(); }
            catch { return null; }
        }

        /// <summary>把诊断信息压成一行(给控制台/UI 显示)。</summary>
        public static string Summary()
        {
            try
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "SDK v{0} | 主线程 {1} 帧={2} | 相机 {3} 台(主={4}) | 场景 {5} | Cinemachine {6} | 输入 {7} | UI 渲染 {8}",
                    SdkVersion.Current,
                    MainThread.MainThreadId,
                    MainThread.FrameCount,
                    CameraService.GetAllCameras(true).Length,
                    CameraService.HasMainCamera ? "有" : "无",
                    SafeActiveSceneName() ?? "?",
                    CinemachineService.IsAvailable ? "可用" : "不可用",
                    InputService.IsAvailable ? "可用" : "不可用",
                    UiService.IsRenderingAvailable ? "可用" : "不可用");
            }
            catch (Exception e) { return "摘要生成失败: " + e.Message; }
        }
    }
}
