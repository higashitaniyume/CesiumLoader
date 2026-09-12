using System;
using System.Reflection;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// mod 元数据声明: 打在 mod 程序集的 ModEntry 类上, SDK 通过 SdkInfo 读取。
    /// 用法: [ModManifest("我的Mod", "1.0.0", "作者", "这个mod做什么")]
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
    public sealed class ModManifestAttribute : Attribute
    {
        public ModManifestAttribute(string name, string version = "1.0.0", string author = "", string description = "")
        {
            Name = name;
            Version = version;
            Author = author;
            Description = description;
        }

        public string Name { get; }
        public string Version { get; }
        public string Author { get; }
        public string Description { get; }
    }

    /// <summary>
    /// 当前 mod 的信息: 从调用 ModBase.Run / SdkConfig 的 mod 程序集读取 [ModManifest]。
    /// 未声明时回退到程序集名。
    /// </summary>
    public static class SdkInfo
    {
        /// <summary>读取指定程序集上声明的 mod 元数据; 未声明时返回基于程序集名的默认值。</summary>
        public static ModManifestAttribute ManifestOf(Assembly assembly)
        {
            if (assembly != null)
            {
                try
                {
                    var attr = assembly.GetCustomAttribute<ModManifestAttribute>();
                    if (attr != null) return attr;
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
