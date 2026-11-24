using System;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 移动端（安卓 / iOS）平台判定。桌面 SDK 上这几项恒为 false。
    ///
    /// 存在意义与 <see cref="AndroidPlatform"/> 相同：把「有没有 user32/winmm 那套原生后端」
    /// 这个差异收敛到一处，别让每个用到输入/音频的地方各判一次。
    ///
    /// 与安卓一致，ECall 调用必须放进**独立方法**再包 try/catch：
    /// <c>Application.platform</c> 在普通 .NET 宿主（单测、命令行）里是 JIT 期
    /// <c>SecurityException</c>，写在同一个方法里连 catch 都进不去，
    /// 会把 <c>InputService</c> 的静态构造打挂（与 HybridCLR 裁剪异常同一类）。
    /// </summary>
    public static class MobilePlatform
    {
        private static bool? _isIOS;

        /// <summary>
        /// 已经确认拿不到原生后端（user32 / winmm）时为 true。
        ///
        /// iOS 上 <c>Application.platform</c> 可能被裁剪掉（HybridCLR 调引擎属性会抛
        /// <c>MissingMethodException</c>/<c>SecurityException</c>，这里只能当 false），
        /// 于是「按平台判断」会漏判。与其猜平台，不如让第一次构造原生后端失败的代码
        /// 调 <see cref="MarkNativeUnavailable"/> 记下来 —— 后面所有 <see cref="IsMobile"/>
        /// 的读取（音频、触摸）就都走 Unity 后端，不会再每帧抛异常。
        /// </summary>
        private static bool _nativeUnavailable;

        /// <summary>记录「这台机器没有 Win32 / winmm 那套原生后端」。</summary>
        public static void MarkNativeUnavailable()
        {
            _nativeUnavailable = true;
        }

        /// <summary>是否运行在 iOS 上（结果缓存）。</summary>
        public static bool IsIOS
        {
            get
            {
                if (_isIOS.HasValue) return _isIOS.Value;

                bool value;
                try { value = ProbeIOS(); }
                catch { value = false; }

                _isIOS = value;
                return value;
            }
        }

        private static bool ProbeIOS()
        {
            return Application.platform == RuntimePlatform.IPhonePlayer;
        }

        /// <summary>是否运行在安卓上（转发 <see cref="AndroidPlatform.IsAndroid"/>）。</summary>
        public static bool IsAndroid
        {
            get { return AndroidPlatform.IsAndroid; }
        }

        /// <summary>是否移动端。移动端没有 Win32 / winmm 原生后端，只能走 Unity 反射那套。</summary>
        public static bool IsMobile
        {
            get { return IsAndroid || IsIOS || _nativeUnavailable; }
        }
    }
}
