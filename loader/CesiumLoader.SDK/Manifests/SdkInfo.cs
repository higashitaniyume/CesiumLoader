using System;
using System.Reflection;
using System.Text;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;

namespace CesiumLoader.SDK.Manifests
{
    /// <summary>
    /// 当前 mod 的信息: 从调用 ModBase.Run / SdkConfig 的 mod 程序集读取 [ModManifest]。
    /// 未声明时回退到程序集名。
    /// </summary>
    public static class SdkInfo
    {
        /// <summary>读取指定程序集上声明的 mod 元数据; 未声明时返回基于程序集名的默认值。
        ///
        /// 权威位置: **程序集级** [assembly: ModManifest(...)] —— 读取不触发类型加载,
        /// 在 HybridCLR 解释器 / 引用缺失环境下可靠。
        /// 兼容: 也尝试类级(ModEntry 上的声明), 但类级遍历可能因引用程序集缺失失败,
        /// 失败时静默回退 —— 因此新 mod 请声明在程序集级。
        /// </summary>
        public static ModManifestAttribute ManifestOf(Assembly assembly)
        {
            if (assembly != null)
            {
                // ① 程序集级(权威; 不触发类型加载)
                try
                {
                    var attr = assembly.GetCustomAttribute<ModManifestAttribute>();
                    if (attr != null) return attr;
                }
                catch { }
                // ② 类级(兼容旧 mod; 遍历失败时回退)
                try
                {
                    foreach (var t in assembly.GetTypes())
                    {
                        var a = t.GetCustomAttribute<ModManifestAttribute>(false);
                        if (a != null) return a;
                    }
                }
                catch { }
            }
            var name = assembly?.GetName().Name ?? "Unknown";
            return new ModManifestAttribute(name);
        }

        /// <summary>调用者程序集(一般就是 mod 自己的程序集)。</summary>
        public static Assembly CallingAssembly()
        {
            // 优先 GetCallingAssembly()(轻量, 不受解释器栈遍历影响);
            // 兜底: 找第一个非 SDK 程序集(可能包含 SDK 自身帧, 用 StackTrace 补充)
            try
            {
                var calling = Assembly.GetCallingAssembly();
                if (calling != null && calling != typeof(SdkInfo).Assembly)
                    return calling;
            }
            catch { }

            try
            {
                var stack = new System.Diagnostics.StackTrace();
                for (int i = 0; i < stack.FrameCount; i++)
                {
                    var method = stack.GetFrame(i)?.GetMethod();
                    var asm = method?.DeclaringType?.Assembly;
                    if (asm != null && asm != typeof(SdkInfo).Assembly)
                        return asm;
                }
            }
            catch { }
            return Assembly.GetCallingAssembly();
        }
    }
}
