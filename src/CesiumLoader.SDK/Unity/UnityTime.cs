using System;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// Unity 时间封装(空安全, 但必须在主线程调用)。
    ///
    /// 与 <c>Time</c> 的区别: 全部经过 <see cref="UnityCall"/> 的 ECall 隔离层,
    /// 任何异常返回 0 / 默认值, 不会因为 Unity 尚未初始化或成员被裁剪而把 mod 弄崩。
    ///
    /// 注意: 这里<b>不能</b>写成 <c>try { return Time.deltaTime; } catch { ... }</c> ——
    /// ECall 的失败发生在方法 JIT 时, 会绕过同一个方法里的 try/catch。必须由 UnityCall 中转。
    /// </summary>
    public static class UnityTime
    {
        /// <summary>本帧耗时(秒)。</summary>
        public static float DeltaTime
        {
            get { return UnityCall.DeltaTime(); }
        }

        /// <summary>本帧耗时(不受 timeScale 影响)。</summary>
        public static float UnscaledDeltaTime
        {
            get { return UnityCall.UnscaledDeltaTime(); }
        }

        /// <summary>固定步长耗时。</summary>
        public static float FixedDeltaTime
        {
            get { return UnityCall.FixedDeltaTime(); }
        }

        /// <summary>自游戏开始累计的时间(受 timeScale 缩放)。</summary>
        public static float TimeSinceLevelLoad
        {
            get { return UnityCall.GameTime(); }
        }

        /// <summary>
        /// 真实运行时间(不受 timeScale 影响)。
        /// Unity 时间不可用时退回进程运行秒数(仍然单调递增, 可用于计时/超时)。
        /// </summary>
        public static float RealtimeSinceStartup
        {
            get { return UnityCall.RealtimeSinceStartup(); }
        }

        /// <summary>自游戏开始经过的帧数。</summary>
        public static int FrameCount
        {
            get { return UnityCall.FrameCount(); }
        }

        /// <summary>时间缩放。</summary>
        public static float TimeScale
        {
            get { return UnityCall.TimeScale(); }
            set { UnityCall.SetTimeScale(value); }
        }

        /// <summary>恢复时间缩放为 1。</summary>
        public static void ResetTimeScale()
        {
            TimeScale = 1f;
        }

        /// <summary>插值系数(用于平滑)。</summary>
        public static float SmoothDeltaTime
        {
            get { return UnityCall.SmoothDeltaTime(); }
        }
    }
}
