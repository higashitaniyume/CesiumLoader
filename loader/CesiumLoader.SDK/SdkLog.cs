using System;
using System.IO;

namespace CesiumLoader.SDK
{
    /// <summary>日志级别。</summary>
    public enum SdkLogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
        /// <summary>致命错误(新增, 原枚举值未变, 保持兼容)。</summary>
        Fatal = 4
    }

    /// <summary>
    /// 日志: 写到 CESIUM_LOG_DIR/activity-mod.log, 由加载器(winmm.dll)转发到控制台窗口。
    /// 所有 mod 共用同一文件(当前加载器只转发这一个文件)。
    /// 分级: Debug/Info/Warn/Error; 可通过环境变量 CESIUM_LOG_LEVEL 过滤(默认 Info, 只显示 Info 及以上)。
    ///
    /// 故障体验: ReportCrash 把异常完整堆栈写到独立 mod-errors.log(不经过日志级别过滤),
    /// 方便定位 mod 崩溃; CrashGuard 包装回调, 异常不外泄到游戏。
    /// </summary>
    public static class SdkLog
    {
        private static string _logFile;
        private static string _errorFile;
        private static readonly object _lock = new object();
        private static SdkLogLevel _minLevel = ReadMinLevel();

        /// <summary>
        /// 最低输出级别(默认 Info, 也可用环境变量 CESIUM_LOG_LEVEL 设置)。
        /// 运行期可修改, 便于 mod/诊断临时打开 Debug。
        /// </summary>
        public static SdkLogLevel MinLevel
        {
            get { return _minLevel; }
            set { _minLevel = value; }
        }

        private static SdkLogLevel ReadMinLevel()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable("CESIUM_LOG_LEVEL");
                if (string.IsNullOrEmpty(raw)) return SdkLogLevel.Info;

                // 显式比较, 不用 Enum.TryParse<SdkLogLevel>:
                // 泛型 Enum.TryParse 的实例化可能被 HybridCLR 的 AOT 裁剪, 而 SdkLog 是
                // 所有 SDK 代码的必经路径, 这里不能出现"只在游戏内才炸"的 API。
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "debug": return SdkLogLevel.Debug;
                    case "info": return SdkLogLevel.Info;
                    case "warn":
                    case "warning": return SdkLogLevel.Warn;
                    case "error": return SdkLogLevel.Error;
                    case "fatal": return SdkLogLevel.Fatal;
                    default: return SdkLogLevel.Info;
                }
            }
            catch { }
            return SdkLogLevel.Info;
        }

        /// <summary>初始化日志文件路径(可多次调用, 幂等)。</summary>
        public static void Init()
        {
            if (_logFile != null) return;
            try
            {
                string dir = Environment.GetEnvironmentVariable("CESIUM_LOG_DIR");
                if (string.IsNullOrEmpty(dir))
                {
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AstralParty_ModLoader", "logs");
                }
                Directory.CreateDirectory(dir);
                _logFile = Path.Combine(dir, "activity-mod.log");
                _errorFile = Path.Combine(dir, "mod-errors.log");
            }
            catch { _logFile = "activity-mod.log"; }
        }

        /// <summary>
        /// 报告 mod 异常: 完整堆栈写到 mod-errors.log(独立文件, 不受日志级别过滤),
        /// 并同时输出一条 ERR 日志。调用方应捕获异常后调用。
        /// </summary>
        /// <param name="modId">mod 程序集名/标识。</param>
        /// <param name="context">出错位置描述(如 "OnCardUsed 事件处理")。</param>
        /// <param name="ex">异常。</param>
        public static void ReportCrash(string modId, string context, Exception ex)
        {
            try
            {
                Init();
                string head = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{modId}] {context}: {ex}";
                lock (_lock)
                {
                    if (_errorFile != null)
                        File.AppendAllText(_errorFile, head + "\r\n" + new string('-', 60) + "\r\n");
                }
                Log(SdkLogLevel.Error, modId, context + ": " + ex?.Message);
            }
            catch { }
        }

        /// <summary>
        /// 异常防护包装: 执行 action, 异常被捕获并 ReportCrash(不向外抛)。
        /// 用于事件回调等不应让异常外泄到游戏的场景。
        /// </summary>
        public static void CrashGuard(string modId, string context, Action action)
        {
            try { action(); }
            catch (Exception ex) { ReportCrash(modId, context, ex); }
        }

        /// <summary>写一行日志(默认 Info 级别, 带时间戳 + 来源前缀)。兼容旧 API。</summary>
        public static void Write(string line)
        {
            Write("SDK", line);
        }

        /// <summary>写一行日志, 指定来源标签(便于区分多个 mod)。默认 Info 级别。兼容旧 API。</summary>
        public static void Write(string tag, string line)
        {
            Log(SdkLogLevel.Info, tag, line);
        }

        /// <summary>Debug 级别日志(默认被过滤, 设 CESIUM_LOG_LEVEL=Debug 才显示)。</summary>
        public static void Debug(string tag, string line) => Log(SdkLogLevel.Debug, tag, line);

        /// <summary>普通信息日志。</summary>
        public static void Info(string tag, string line) => Log(SdkLogLevel.Info, tag, line);

        /// <summary>警告日志。</summary>
        public static void Warn(string tag, string line) => Log(SdkLogLevel.Warn, tag, line);

        /// <summary>警告日志(与 Warn 等价, 便于对齐常见命名习惯)。</summary>
        public static void Warning(string tag, string line) => Log(SdkLogLevel.Warn, tag, line);

        /// <summary>错误日志。</summary>
        public static void Error(string tag, string line) => Log(SdkLogLevel.Error, tag, line);

        /// <summary>致命错误日志。</summary>
        public static void Fatal(string tag, string line) => Log(SdkLogLevel.Fatal, tag, line);

        /// <summary>报告致命错误并附异常堆栈。</summary>
        public static void Fatal(string tag, string context, Exception ex)
        {
            ReportCrash(tag, context, ex);
            Log(SdkLogLevel.Fatal, tag, context);
        }

        private static void Log(SdkLogLevel level, string tag, string line)
        {
            if (level < _minLevel) return;
            Init();
            if (line == null) return;
            try
            {
                var levelTag = level switch
                {
                    SdkLogLevel.Debug => "DBG",
                    SdkLogLevel.Warn => "WRN",
                    SdkLogLevel.Error => "ERR",
                    SdkLogLevel.Fatal => "FTL",
                    _ => "INF"
                };
                lock (_lock)
                {
                    File.AppendAllText(_logFile,
                        $"[{DateTime.Now:HH:mm:ss.fff}] [{levelTag}] [{tag}] {line}\r\n");
                }
            }
            catch { }
        }
    }
}
