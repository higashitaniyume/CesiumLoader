using System.Globalization;
using CesiumLoader.SDK.Internals;

namespace CesiumLoader.SDK.Engine
{
    /// <summary>
    /// Unity 帧率 / 垂直同步封装(空安全, 必须在主线程调用)。
    ///
    /// 与直接写 <c>Application.targetFrameRate</c> / <c>QualitySettings.vSyncCount</c> 的区别:
    /// 全部经过 <see cref="UnityCall"/> 的 ECall 隔离层, Unity 不可用或成员被裁剪时返回
    /// <c>false</c> / 默认值而不抛异常; 调用方可以据返回值判断"这次到底有没有写进去"。
    ///
    /// 注意:
    ///  - 这里改的是<b>运行时</b>帧率, 与游戏存档里的画质/帧率设置无关。游戏在启动、切换画质、
    ///    打开设置面板或退出节能模式时会重新写入自己的值, 需要重新施加才会保持;
    ///  - 目标帧率只在垂直同步关闭(<see cref="VSync"/> == 0)时生效:
    ///    <see cref="Unlimited"/> 配合 <see cref="SetVSync"/>(0) 才是不限帧率;
    ///  - Unity 只保证 <see cref="Unlimited"/> 表示"不限帧"; 正数上限由平台实现, 可能有取整/节流误差。
    /// </summary>
    public static class UnityFrameRate
    {
        /// <summary>不限帧率(Application.targetFrameRate = -1)。</summary>
        public const int Unlimited = -1;

        /// <summary>自定义上限允许的最大值(再高的值没有实际意义, 也避免把异常数据写进引擎)。</summary>
        public const int MaxCustom = 1000;

        /// <summary>当前目标帧率; Unity 不可用时 0。</summary>
        public static int Target
        {
            get { return UnityCall.TargetFrameRate(); }
        }

        /// <summary>当前垂直同步计数(0 = 关闭); Unity 不可用时 0。</summary>
        public static int VSync
        {
            get { return UnityCall.VSyncCount(); }
        }

        /// <summary>
        /// 设置目标帧率: <see cref="Unlimited"/> 或 1–<see cref="MaxCustom"/>。
        /// 超出范围返回 false 且不写入; Unity 不可用时也返回 false。
        /// </summary>
        public static bool SetTarget(int framesPerSecond)
        {
            if (framesPerSecond != Unlimited &&
                (framesPerSecond < 1 || framesPerSecond > MaxCustom))
                return false;

            return UnityCall.SetTargetFrameRate(framesPerSecond);
        }

        /// <summary>设置为不限帧率(等价于 <c>SetTarget(Unlimited)</c>)。</summary>
        public static bool SetUnlimited()
        {
            return SetTarget(Unlimited);
        }

        /// <summary>设置垂直同步计数(0–4, 0 = 关闭); 超出范围返回 false 且不写入。</summary>
        public static bool SetVSync(int count)
        {
            if (count < 0 || count > 4) return false;
            return UnityCall.SetVSyncCount(count);
        }

        /// <summary>单行状态描述(日志/诊断用)。</summary>
        public static string Describe()
        {
            int target = Target;
            return "targetFrameRate=" +
                   (target < 0 ? "不限(" + target.ToString(CultureInfo.InvariantCulture) + ")" : target.ToString(CultureInfo.InvariantCulture)) +
                   " vSyncCount=" + VSync.ToString(CultureInfo.InvariantCulture);
        }
    }
}
