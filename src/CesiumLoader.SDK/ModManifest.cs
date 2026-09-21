using System;
using System.Reflection;
using System.Text;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// mod 权限位。敏感能力默认关闭, mod 必须声明才可用, 且可在配置中逐项禁用。
    ///
    /// <para>
    /// <b>注意: 权限机制已取消门控(见 <see cref="Permissions"/>)</b> —— 这些位现在只用于
    /// Manifest 声明 / 工具展示 / 安全审计 / 让用户知道"这个 mod 会动到什么"。声明不会
    /// 阻止任何调用, 也不会因为没有声明就失败。
    /// </para>
    ///
    /// <para>
    /// 数值兼容性: 前 4 个位的历史数值保持不变(1/2/4/8), 新增类别一律追加在
    /// 高位上 —— 旧加载器/旧工具读到的仍是它们认识的那几个位。
    /// </para>
    /// </summary>
    [Flags]
    public enum ModPermission
    {
        /// <summary>无特殊权限(默认)。</summary>
        None = 0,

        /// <summary>读取对局状态(玩家/手牌/事件订阅)。默认授予, 只读无副作用。</summary>
        ReadGameState = 1 << 0,

        /// <summary>向服务器发送操作 (GameActions: 投骰/移动/用牌等)。
        /// 敏感: 会真实影响对局, 默认关闭。</summary>
        GameActions = 1 << 1,

        /// <summary>变速 (SpeedHack: 改变游戏时间流速)。
        /// 敏感: 联机对局可能触发服务器检测, 默认关闭。</summary>
        SpeedHack = 1 << 2,

        /// <summary>写文件 (mods 目录内配置/日志)。默认授予, 限 mods 目录。</summary>
        FileWrite = 1 << 3,

        /// <summary>修改对局/游戏状态(内存态改写, 不经过服务器 RPC)。比 GameActions 更危险。</summary>
        ModifyGameState = 1 << 4,

        /// <summary>相机控制(创建/驱动/接管相机, 改变渲染视角)。</summary>
        Camera = 1 << 5,

        /// <summary>读取与独占输入(键盘/鼠标)。</summary>
        Input = 1 << 6,

        /// <summary>创建 mod UI(叠加层/窗口/通知)。</summary>
        UI = 1 << 7,

        /// <summary>文件系统访问(mods 目录之外的读写)。</summary>
        FileSystem = 1 << 8,

        /// <summary>网络访问(自行发起连接/请求, 与游戏流量无关)。</summary>
        Network = 1 << 9,

        /// <summary>调试能力(调试叠加层/诊断转储/暂停与步进)。</summary>
        Debug = 1 << 10,
    }

    /// <summary>
    /// mod 依赖声明: 依赖的另一个 mod(按程序集名) + 最低版本。
    /// </summary>
    public sealed class ModDependency
    {
        public ModDependency(string id, string minVersion = null)
        {
            Id = id;
            MinVersion = minVersion;
        }

        /// <summary>被依赖 mod 的程序集名(不带 .dll)。</summary>
        public string Id { get; }

        /// <summary>最低版本(SemVer, 可空 = 任意版本)。</summary>
        public string MinVersion { get; }
    }

    /// <summary>
    /// mod 元数据声明: 打在 mod 程序集的 ModEntry 类上。
    ///
    /// 用法:
    ///   [ModManifest("我的Mod", "1.0.0", "作者", "描述",
    ///       Permissions = ModPermission.ReadGameState,
    ///       SdkVersion = "2.1.5",
    ///       Dependencies = new[] { new ModDependency("OtherMod", "1.0.0") })]
    ///
    /// 敏感权限 (GameActions / SpeedHack) 默认关闭, 声明只是"请求"——最终是否
    /// 授予由加载器 + mods 配置决定, 可在 mods\*.permissions.json 中逐项覆盖。
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

        /// <summary>mod 请求的权限(默认 None; ReadGameState/FileWrite 默认授予, 敏感项默认拒绝)。</summary>
        public ModPermission Permissions { get; set; } = ModPermission.None;

        /// <summary>依赖的 SDK 最低版本(SemVer, 如 "2.1.5")。加载器会检查兼容性。</summary>
        public string SdkVersion { get; set; }

        /// <summary>依赖的其他 mod(程序集名 + 最低版本)。加载器按依赖顺序加载。</summary>
        public ModDependency[] Dependencies { get; set; } = Array.Empty<ModDependency>();
    }

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

    /// <summary>
    /// SDK 版本与兼容信息。mod 通过 [ModManifest(SdkVersion=...)] 声明需要的版本,
    /// 加载器在加载时校验; 运行时可用 VersionAtLeast 做防御式检查。
    /// </summary>
    public static class SdkVersion
    {
        /// <summary>当前 SDK 版本 (SemVer)。与 CesiumLoader.SDK.csproj 的 Version 保持一致。</summary>
        public const string Current = "2.1.5";

        /// <summary>加载器原生层版本(与 version.dll 构建对应)。</summary>
        public const string LoaderVersion = "2.1.5";

        /// <summary>最小可接受的 mod 声明的 SDK 版本。</summary>
        public static bool Accepts(string modSdkVersion)
        {
            if (string.IsNullOrEmpty(modSdkVersion)) return true;   // 未声明 = 兼容
            return Compare(modSdkVersion, Current) <= 0;
        }

        /// <summary>mod 声明的 SDK 版本是否 ≥ 给定版本(运行时防御检查)。</summary>
        public static bool DeclaredAtLeast(Assembly asm, string minVersion)
        {
            try
            {
                var m = SdkInfo.ManifestOf(asm);
                return !string.IsNullOrEmpty(m.SdkVersion) && Compare(m.SdkVersion, minVersion) >= 0;
            }
            catch { return false; }
        }

        /// <summary>SemVer 比较(a 比 b 大返回 &gt;0, 相等 0, 小 &lt;0)。只比较主.次.修订数字。</summary>
        public static int Compare(string a, string b)
        {
            int[] Pa = Parse(a), Pb = Parse(b);
            for (int i = 0; i < 3; i++)
            {
                if (Pa[i] != Pb[i]) return Pa[i] > Pb[i] ? 1 : -1;
            }
            return 0;
        }

        private static int[] Parse(string v)
        {
            var r = new int[3];
            if (string.IsNullOrEmpty(v)) return r;
            var parts = v.Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                int n;
                if (int.TryParse(parts[i].Trim(), out n)) r[i] = n;
            }
            return r;
        }
    }
}
