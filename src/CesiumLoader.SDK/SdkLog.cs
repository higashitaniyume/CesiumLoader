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
        Error = 3
    }

    /// <summary>
    /// 日志: 写到 CESIUM_LOG_DIR/activity-mod.log, 由加载器(winmm.dll)转发到控制台窗口。
    /// 所有 mod 共用同一文件(当前加载器只转发这一个文件)。
    /// 分级: Debug/Info/Warn/Error; 可通过环境变量 CESIUM_LOG_LEVEL 过滤(默认 Info, 只显示 Info 及以上)。
    /// </summary>
    public static class SdkLog
    {
        private static string _logFile;
        private static readonly object _lock = new object();
        private static readonly SdkLogLevel _minLevel = ReadMinLevel();

        private static SdkLogLevel ReadMinLevel()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable("CESIUM_LOG_LEVEL");
                if (!string.IsNullOrEmpty(raw) && Enum.TryParse<SdkLogLevel>(raw, true, out var level))
                    return level;
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
            }
            catch { _logFile = "activity-mod.log"; }
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

        /// <summary>错误日志。</summary>
        public static void Error(string tag, string line) => Log(SdkLogLevel.Error, tag, line);

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
