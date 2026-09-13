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
        /// <returns>是否成功(引擎不可用、倍率非法或 mod 无 SpeedHack 权限时 false)。</returns>
        public static bool SetSpeed(double speed)
        {
            if (speed <= 0.0 || speed > 100.0) return false;
            if (!Permissions.Require(ModPermission.SpeedHack, "SpeedHack.SetSpeed")) return false;
            try { return IsAvailable && ap_speed_set(speed); }
            catch { return false; }
        }

        /// <summary>恢复正常速度 (1.0)。</summary>
        public static bool Reset() => SetSpeed(1.0);
    }
}
