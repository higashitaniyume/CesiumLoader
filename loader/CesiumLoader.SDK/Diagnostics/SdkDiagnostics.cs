using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumLoader.SDK.Cameras;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Runtime;
using CesiumLoader.SDK.Scenes;
using CesiumLoader.SDK.Scheduling;
using CesiumLoader.SDK.UserInterface;

namespace CesiumLoader.SDK.Diagnostics
{
    /// <summary>
    /// 诊断入口。
    ///
    /// <code>
    /// using CesiumLoader.SDK.Diagnostics;
    ///
    /// SdkDiagnostics.Dump();            // 全量: 运行时 + 相机 + 场景
    /// SdkDiagnostics.DumpCameraInfo();  // 只看相机
    /// SdkDiagnostics.DumpSceneInfo();   // 只看场景
    /// </code>
    ///
    /// 输出位置: 加载器本次启动的日志文件(<c>CESIUM_LOG_FILE</c>, 形如
    /// <c>CESIUM_LOG_DIR\cesium-loader-&lt;yyyyMMdd&gt;-&lt;HHmmss&gt;-&lt;pid&gt;.log</c>;
    /// 与本次启动的 activity 日志同目录),
    /// 同时把摘要打进 activity 日志(会转发到控制台)。
    ///
    /// 加载器没给 <c>CESIUM_LOG_FILE</c> 时(旧版加载器)回退到 <c>CESIUM_LOG_DIR\cesium-loader.log</c>。
    /// 每次启动一套日志文件, 所以诊断转储落在"这次启动"的文件里, 不会和上一次启动的内容混在一起。
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

        /// <summary>旧版加载器的回退文件名(没有 CESIUM_LOG_FILE 时用)。</summary>
        public const string DumpFileName = "cesium-loader.log";

        /// <summary>诊断日志完整路径(无法定位时返回文件名)。</summary>
        public static string GetDumpFilePath()
        {
            try
            {
                // 首选加载器给的"本次启动的日志文件"完整路径
                string file = Environment.GetEnvironmentVariable("CESIUM_LOG_FILE");
                if (!string.IsNullOrEmpty(file)) return file;

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
