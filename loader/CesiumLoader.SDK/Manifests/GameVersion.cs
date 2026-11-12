using System;
using System.Reflection;
using CesiumLoader.SDK.Logging;

namespace CesiumLoader.SDK.Manifests
{
    /// <summary>
    /// SDK 编译时对应的<b>游戏程序集身份</b>, 与运行中的游戏程序集比对。
    ///
    /// 为什么不用版本号(3.3.0): 游戏热更换的是热更程序集(<c>AstralParty.Runtime.dll</c>),
    /// 主版本号可能纹丝不动 —— 今天这次热更就是 3.3.0 → 3.3.0, 但协议成员被删了
    /// (<c>MovePointBuffS2C.Attrs</c> 没了)。用程序集 MVID 判定"这套 SDK/mod 是不是
    /// 为当前这个游戏程序集编译的", 才拦得住这种"同版本号、不同程序集"的情况。
    ///
    /// 判定不通过时: 内置 mod 一律不启动(只留加载器本体与控制台), 并在日志里说明原因。
    /// 宁可什么都不做, 也不要让带着旧协议假设的 mod 在游戏里把整局卡死。
    ///
    /// 维护: 换 <c>refs/game/AstralParty.Runtime.dll</c> 时必须同步更新
    /// <see cref="CompiledGameAssemblyMvid"/>(单元测试与打包脚本都会校验)。
    /// </summary>
    public static class GameVersion
    {
        /// <summary>编译时 <c>refs/game/AstralParty.Runtime.dll</c> 的 MVID(小写, 带连字符)。</summary>
        public const string CompiledGameAssemblyMvid = "eb27445a-329c-4c13-9f2d-563cc6cff756";

        private static readonly object Gate = new object();
        private static string _running;
        private static bool _probed;

        /// <summary>运行中游戏程序集的 MVID; 读不到时为 null(判定为"不拦")。</summary>
        public static string RunningGameAssemblyMvid
        {
            get
            {
                lock (Gate)
                {
                    if (!string.IsNullOrEmpty(_running)) return _running;
                    _running = Probe();
                    return _running;
                }
            }
        }

        /// <summary>当前游戏程序集与编译时的是否一致。读不到运行中的 MVID 时返回 true(不拦)。</summary>
        public static bool Matches
        {
            get
            {
                string running = RunningGameAssemblyMvid;
                if (string.IsNullOrEmpty(running)) return true;
                return string.Equals(running, CompiledGameAssemblyMvid, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>一句话说明当前状态(写进日志/提示)。</summary>
        public static string Describe()
        {
            string running = RunningGameAssemblyMvid;
            if (string.IsNullOrEmpty(running)) return "游戏程序集版本未知(不拦截)";
            if (Matches) return "游戏程序集匹配";
            return "游戏程序集不匹配: 本包编译于 " + CompiledGameAssemblyMvid + ", 当前 " + running +
                   " —— 这次游戏更新换了热更程序集, 需要更新加载器/模组包";
        }

        /// <summary>读运行中游戏程序集的 MVID(读不到返回 null)。</summary>
        /// <remarks>
        /// 这里**不能**写 <c>typeof(某个游戏类型).Assembly</c>: 程序集没加载时, JIT 会在进
        /// 方法体之前就抛 TypeLoadException, try/catch 拦不住(SDK 文档里记过同一个坑)。
        /// 所以改成在已加载的程序集列表里按名字找。
        /// </remarks>
        private static string Probe()
        {
            try
            {
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loaded.Length; i++)
                {
                    Assembly assembly = loaded[i];
                    if (assembly == null) continue;
                    string name = assembly.GetName().Name;
                    if (!string.Equals(name, "AstralParty.Runtime", StringComparison.OrdinalIgnoreCase)) continue;
                    return assembly.ManifestModule.ModuleVersionId.ToString("D");
                }
                return null;   // 游戏程序集还没加载(热更前) → 不拦
            }
            catch (Exception e)
            {
                SdkLog.Warn("GameVersion", "读不到运行中的游戏程序集版本(不拦截): " + e.Message);
                return null;
            }
        }
    }
}
