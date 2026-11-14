using CesiumLoader.SDK.Logging;

namespace CesiumLoader.SDK.Mobile
{
    /// <summary>
    /// 安卓端"系统通知"入口（安卓编译的 mod 会引用）。
    ///
    /// 当前没有接入系统通知栏：<see cref="IsAvailable"/> 恒为 false，
    /// <see cref="Show"/> 只写 SDK 日志，mod 应自行回退到游戏内提示（UiService）。
    /// 将来接了通知通道再改这里，mod 不用重新编译。
    /// </summary>
    public static class MobileNotice
    {
        /// <summary>是否可用。当前不可用。</summary>
        public static bool IsAvailable
        {
            get { return false; }
        }

        /// <summary>是否支持提示音。当前不支持。</summary>
        public static bool SupportsSound
        {
            get { return false; }
        }

        /// <summary>不可用原因，供 mod 写日志或展示。</summary>
        public static string UnavailableReason
        {
            get { return "安卓端尚未接入系统通知"; }
        }

        /// <summary>弹一条通知；当前降级为 SDK 日志。</summary>
        public static void Show(string message)
        {
            SdkLog.Info("MobileNotice", message ?? string.Empty);
        }

        /// <summary>
        /// 通知轮询入口（安卓编译的 mod 每帧会调）。当前没有通知队列，什么都不做；
        /// 保留这个成员是为了让已编译的 mod 绑定成功（缺它会每帧抛 MethodNotFind）。
        /// </summary>
        public static void Tick()
        {
        }
    }
}
