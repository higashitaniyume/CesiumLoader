using System;
using System.Runtime.InteropServices;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 游戏变速 (speedhack): 让 mod 通过原生加载器控制游戏时间流速。
    ///
    /// 原理: 加载器 (version.dll) 在进程启动时 inline hook 了系统时间函数
    /// (GetTickCount / GetTickCount64 / timeGetTime / QueryPerformanceCounter),
    /// 按倍率缩放返回值 —— 游戏感知的时间流逝随之变快/变慢。
    /// 本类通过 P/Invoke 调用加载器的 ap_speed_* 导出设置倍率。
    ///
    /// ⚠️ 警告:
    ///  - 变速会影响游戏感知的**所有**时间(动画/演出/回合/网络超时)。
    ///  - 联机对局变速可能导致本地与服务器时间戳不一致, 有断线/封号风险, 慎用。
    ///  - 倍率范围 (0, 100], 1.0 = 正常。
    /// </summary>
    public static class SpeedHack
    {
        private const string LoaderDll = "version.dll";

        /// <summary>热键调倍率的下限(比这更慢游戏基本等于卡住)。</summary>
        public const double MinSpeed = 0.1;

        /// <summary>热键调倍率的上限(比这更快容易触发服务器异常节奏判定, 也更容易崩)。</summary>
        public const double MaxSpeed = 10.0;

        /// <summary>热键调倍率的默认步进。</summary>
        public const double DefaultSpeedStep = 0.5;

        [DllImport(LoaderDll, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ap_speed_set(double speed);

        [DllImport(LoaderDll, CallingConvention = CallingConvention.Winapi)]
        private static extern double ap_speed_get();

        [DllImport(LoaderDll, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ap_speed_active();

        private static bool _availabilityChecked;
        private static bool _available;

        /// <summary>变速引擎是否可用(加载器已安装 hook)。</summary>
        public static bool IsAvailable
        {
            get
            {
                if (!_availabilityChecked)
                {
                    _availabilityChecked = true;
                    try { _available = ap_speed_active(); }
                    catch { _available = false; }
                }
                return _available;
            }
        }

        /// <summary>当前倍率(引擎不可用时恒为 1.0)。</summary>
        public static double Speed
        {
            get
            {
                try { return IsAvailable ? ap_speed_get() : 1.0; }
                catch { return 1.0; }
            }
        }

        /// <summary>
        /// 设置倍率。
        /// </summary>
        /// <param name="speed">倍率, 范围 (0, 100], 1.0 = 正常, 2.0 = 2 倍速, 0.5 = 半速。</param>
        /// <returns>是否成功(引擎不可用、加载器没有相应导出或倍率非法时 false; 不抛异常)。</returns>
        public static bool SetSpeed(double speed)
        {
            // NaN 必须显式挡掉: 它和任何数比较都是 false, "speed <= 0 || speed > 100" 拦不住它,
            // 而把 NaN 传进引擎会让虚拟时间变成 NaN(游戏时间彻底坏掉)。
            if (double.IsNaN(speed)) return false;
            if (speed <= 0.0 || speed > 100.0) return false;
            if (!Permissions.Require(ModPermission.SpeedHack, "SpeedHack.SetSpeed")) return false;
            try { return IsAvailable && ap_speed_set(speed); }
            catch { return false; }
        }

        /// <summary>恢复正常速度 (1.0)。</summary>
        public static bool Reset() => SetSpeed(1.0);

        /// <summary>
        /// 把倍率夹到 [<paramref name="min"/>, <paramref name="max"/>] 并规整小数位。
        /// 非法输入(NaN/Inf)回归 1.0; min/max 写反了自动交换。
        /// </summary>
        public static double ClampSpeed(double speed, double min = MinSpeed, double max = MaxSpeed)
        {
            if (double.IsNaN(min) || double.IsInfinity(min) || min <= 0.0) min = MinSpeed;
            if (double.IsNaN(max) || double.IsInfinity(max) || max <= 0.0) max = MaxSpeed;
            if (min > max) { double tmp = min; min = max; max = tmp; }

            if (double.IsNaN(speed) || double.IsInfinity(speed)) return 1.0;
            if (speed < min) speed = min;
            if (speed > max) speed = max;

            // 0.1 这类步进累加会飘成 2.5000000000000004, 规整到 3 位小数
            return Math.Round(speed, 3, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 纯计算(不碰加载器, 可离线测试): 按一次热键把倍率朝 <paramref name="delta"/> 方向调整
        /// <paramref name="step"/>, 结果按 [<paramref name="min"/>, <paramref name="max"/>] 夹紧。
        ///
        /// 容错: <paramref name="step"/> ≤ 0 / NaN / Inf 时退回 <see cref="DefaultSpeedStep"/>;
        /// <paramref name="min"/>/<paramref name="max"/> 非法时退回 <see cref="MinSpeed"/>/<see cref="MaxSpeed"/>。
        /// 返回值与 <paramref name="current"/> 相同即表示已到极限(调用方据此提示, 不必写盘)。
        /// </summary>
        public static double StepSpeed(double current, double delta, double step = DefaultSpeedStep,
            double min = MinSpeed, double max = MaxSpeed)
        {
            if (double.IsNaN(current) || double.IsInfinity(current)) current = 1.0;
            if (double.IsNaN(delta) || double.IsInfinity(delta) || delta == 0.0)
                return ClampSpeed(current, min, max);
            if (double.IsNaN(step) || double.IsInfinity(step) || step <= 0.0) step = DefaultSpeedStep;
            return ClampSpeed(current + delta * step, min, max);
        }
    }
}
