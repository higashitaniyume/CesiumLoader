using CesiumLoader.SDK.Logging;

namespace CesiumLoader.SDK.Mobile
{
    /// <summary>
    /// 安卓触摸相机控制（安卓编译的 FreeCameraMod 会引用）。
    ///
    /// 目前只做"不接管"：<see cref="UpdateCamera"/> 返回 false 表示没有消费输入，
    /// mod 可以继续用自己那一套（或干脆不启用触摸）。手势语义定下来之后在这里实现，
    /// 不需要 mod 重新编译。
    /// </summary>
    public static class MobileTouch
    {
        private static object _camera;
        private static bool _warned;

        /// <summary>绑定要控制的相机（mod 传入的是它自己的相机对象）。</summary>
        public static void AttachCamera(object camera)
        {
            _camera = camera;
            SdkLog.Info("MobileTouch", "已绑定相机，触摸手势尚未接入");
        }

        /// <summary>每帧调用；返回是否消费了本次输入。</summary>
        public static bool UpdateCamera(object camera)
        {
            if (camera != null) _camera = camera;
            if (!_warned)
            {
                _warned = true;
                SdkLog.Warn("MobileTouch", "安卓触摸相机未接入，UpdateCamera 返回 false");
            }
            return false;
        }
    }
}
