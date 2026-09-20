using System;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 绑定到某个 mod 的日志器: 自动把 ModId 作为标签, 免去每个 mod 手写 tag。
    /// 由 <see cref="CesiumLoader.SDK.ModContext.Logger"/> 提供。
    /// </summary>
    public sealed class ModLogger
    {
        private readonly string _tag;

        internal ModLogger(string tag)
        {
            _tag = string.IsNullOrEmpty(tag) ? "MOD" : tag;
        }

        /// <summary>日志标签(即 ModId)。</summary>
        public string Tag { get { return _tag; } }

        /// <summary>Debug 级(默认被 CESIUM_LOG_LEVEL 过滤)。</summary>
        public void Debug(string message) { SdkLog.Debug(_tag, message); }

        /// <summary>Info 级。</summary>
        public void Info(string message) { SdkLog.Info(_tag, message); }

        /// <summary>Warning 级。</summary>
        public void Warn(string message) { SdkLog.Warn(_tag, message); }

        /// <summary>Warning 级(别名)。</summary>
        public void Warning(string message) { SdkLog.Warning(_tag, message); }

        /// <summary>Error 级。</summary>
        public void Error(string message) { SdkLog.Error(_tag, message); }

        /// <summary>Fatal 级。</summary>
        public void Fatal(string message) { SdkLog.Fatal(_tag, message); }

        /// <summary>记录异常完整堆栈(独立文件 mod-errors.log)。</summary>
        public void ReportCrash(string context, Exception ex) { SdkLog.ReportCrash(_tag, context, ex); }

        /// <summary>异常防护包装。</summary>
        public void CrashGuard(string context, Action action) { SdkLog.CrashGuard(_tag, context, action); }
    }
}
