using System;
using System.IO;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 日志: 写到 CESIUM_LOG_DIR/activity-mod.log, 由加载器(windows.winmm.dll)转发到控制台窗口。
    /// 所有 mod 共用同一文件(当前加载器只转发这一个文件)。
    /// </summary>
    public static class SdkLog
    {
        private static string _logFile;
        private static readonly object _lock = new object();

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

        /// <summary>写一行日志(带时间戳 + 来源前缀)。</summary>
        public static void Write(string line)
        {
            Write("SDK", line);
        }

        /// <summary>写一行日志, 指定来源标签(便于区分多个 mod)。</summary>
        public static void Write(string tag, string line)
        {
            Init();
            if (line == null) return;
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_logFile,
                        $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {line}\r\n");
                }
            }
            catch { }
        }
    }
}
