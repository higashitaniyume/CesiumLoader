using System;
using System.IO;

namespace CesiumLoader.SDK.Logging
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
}
