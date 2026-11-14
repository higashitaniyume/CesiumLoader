using System;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 安卓平台适配层。
    ///
    /// 桌面 SDK 里没有这个类型，但安卓侧编译的 mod（AudioSkin / CardSkin / CharacterSkin 等）
    /// 会引用它；缺了就是 `TypeLoadException: Could not load type 'CesiumLoader.SDK.AndroidPlatform'`。
    /// 这里提供最小可用实现，把与平台相关的差异收敛到一个地方。
    /// </summary>
    public static class AndroidPlatform
    {
        private static bool? _isAndroid;

        /// <summary>
        /// 当前是否运行在 Android 上。
        ///
        /// 必须包 try/catch 并缓存: <c>Application.platform</c> 是 UnityEngine 的 ECall,
        /// 在普通 .NET 宿主(单元测试、命令行)里调用会抛
        /// <c>SecurityException: ECall methods must be packaged into a system module</c>。
        /// 之前没兜住, 直接把 <c>InputService</c> 的静态构造打挂, 牵连 20 个用例失败。
        /// </summary>
        public static bool IsAndroid
        {
            get
            {
                if (_isAndroid.HasValue) return _isAndroid.Value;

                // 调用必须放在**独立方法**里: SecurityException 是 JIT 期抛的,
                // 写在同一个方法里连 try/catch 都进不去(和 HybridCLR 裁剪异常同一类)。
                bool value;
                try { value = ProbeAndroid(); }
                catch { value = false; }

                _isAndroid = value;
                return value;
            }
        }

        private static bool ProbeAndroid()
        {
            return Application.platform == RuntimePlatform.Android;
        }

        /// <summary>
        /// 读环境变量。安卓上进程环境一般拿不到（`setenv` 由加载器在托管层设置，
        /// 走的是 `Environment.SetEnvironmentVariable`），所以这里先读托管环境，
        /// 读不到再看 `/proc/self/environ`。
        /// </summary>
        public static string GetEnvironmentVariable(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            string value = null;
            try { value = Environment.GetEnvironmentVariable(name); }
            catch { value = null; }
            if (!string.IsNullOrEmpty(value)) return value;

            if (!IsAndroid) return null;

            try
            {
                string[] lines = System.IO.File.ReadAllText("/proc/self/environ").Split('\0');
                string prefix = name + "=";
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
                        return lines[i].Substring(prefix.Length);
                }
            }
            catch
            {
                // 读不到就当没有，调用方自行回退。
            }
            return null;
        }
    }
}
