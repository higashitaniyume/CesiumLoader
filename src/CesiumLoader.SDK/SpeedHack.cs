using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 游戏变速 (speedhack): 让 mod 控制游戏时间流速。
    ///
    /// 原理: 加载器 (version.dll) 在进程启动时 inline hook 了系统时间函数
    /// (GetTickCount / GetTickCount64 / timeGetTime / QueryPerformanceCounter),
    /// 按倍率缩放返回值 —— 游戏感知的时间流逝随之变快/变慢。
    ///
    /// <b>通信方式(重要):</b> 本游戏的热更程序集(经 HybridCLR 加载)在运行时
    /// <b>无法 P/Invoke</b> —— 任何 <c>[DllImport]</c> 调用都会抛
    /// <c>NotSupportManaged2NativeFunctionMethod</c>(IL2CPP 只对构建期已知的程序集生成
    /// managed→native thunk), 连 kernel32 的 GetModuleHandleW 都调不通。因此本类
    /// <b>不</b>调用加载器的 ap_speed_* 导出, 而是走加载器的"变速控制文件通道":
    ///
    /// <code>
    ///   &lt;CESIUM_SPEED_DIR&gt;\request.txt   mod → 加载器: 期望倍率(1.000 = 恢复正常, <b>不是</b>关闭引擎)
    ///   &lt;CESIUM_SPEED_DIR&gt;\state.txt     加载器 → mod: version/speed/base/active/hooks
    /// </code>
    ///
    /// 加载器每 100ms 检查一次请求文件, 应用后回写状态文件 —— 所以倍率是"请求后立即返回,
    /// 约 100ms 内生效"(不是同步生效)。需要加载器 ≥ 2.1.5(更早的版本没有这个通道)。
    ///
    /// <b>1.0 的语义:</b> 就是"乘以 1"。hook 一旦装上就常驻, 1.0 时它原样返回真实时间,
    /// 因此从任何倍率切回 1.0 都不涉及卸载/重装 hook, <c>hooks=</c> 也不会掉到 0 ——
    /// 本类<b>没有</b>"关闭变速"这个概念: "恢复正常"只是一次普通的倍率写入。
    ///
    /// ⚠️ 警告:
    ///  - 变速会影响游戏感知的**所有**时间(动画/演出/回合/网络超时)。
    ///  - **别把倍率调太高**: 越高越容易让动画/物理/协程变形、网络节奏对不上, 建议 ≤ 3。
    ///  - 倍率范围 [1.0, 100], 1.0 = 正常。**低于 1 倍被硬性禁止**(见 <see cref="MinSpeed"/>)。
    /// </summary>
    public static class SpeedHack
    {
        /// <summary>
        /// 硬性下限 = 1.0: **不允许减速**。
        ///
        /// 低于 1 倍会让游戏感知的时间变慢, 计时/超时/网络节奏都跟着变形, 实测也容易触发卡顿,
        /// 所以这是一条产品规则而不是"建议值" —— 代码里三处一起拦:
        /// 加载器的请求解析(<c>parse_speed</c>)、加载器的 <c>speedhack_set_speed</c>、
        /// 以及本类的 <see cref="SetSpeed"/>/<see cref="ClampSpeed"/>/<see cref="StepSpeed"/>。
        /// 也就是说即使有人手改 config.json 里的 <c>minSpeed</c>, 也调不出低于 1 倍。
        /// </summary>
        public const double MinSpeed = 1.0;

        /// <summary>热键调倍率的上限(比这更快容易触发服务器异常节奏判定, 也更容易崩)。</summary>
        public const double MaxSpeed = 10.0;

        /// <summary>加载器(原生)允许的倍率上限, 与 speedhack.h 的 kSpeedMax 一致。</summary>
        public const double HardMaxSpeed = 100.0;

        /// <summary>热键调倍率的默认步进。</summary>
        public const double DefaultSpeedStep = 0.5;

        private const string RequestFileName = "request.txt";
        private const string StateFileName = "state.txt";

        // 倍率一律用不变文化写盘: 中文/德语等区域会把小数点写成逗号, 原生侧只认点号。
        private const string SpeedFormat = "0.000";

        private static readonly object _lock = new object();
        private static string _dir;
        private static bool _availabilityChecked;

        // 诊断: 最近一次"读状态失败"和"写请求失败"的具体原因(由 Diagnostics() 打在日志里)。
        private static string _stateError;
        private static string _writeError;
        private static string _unavailableReason;

        // 测试用: 覆盖控制文件目录 / 清缓存(避免依赖进程环境变量与真实游戏目录)。
        internal static string DirectoryOverride { get; set; }

        internal static void ResetCache()
        {
            lock (_lock)
            {
                _availabilityChecked = false;
                _unavailableReason = null;
                _dir = null;
                _stateError = null;
                _writeError = null;
            }
        }

        /// <summary>
        /// 控制文件目录。按候选顺序找一个**真的存在 state.txt** 的目录; 找不到时返回第一个
        /// 非空候选(供报错用)。结果会被缓存, 但每次读取都会验证缓存目录里 state.txt 还在 ——
        /// 否则重扫。这样"加载器比 mod 先写/后写""环境变量读不到"等情况都能自愈。
        /// </summary>
        public static string Directory
        {
            get
            {
                lock (_lock)
                {
                    if (_dir != null && HasStateFile(_dir)) return _dir;
                    _dir = ResolveDirectory();
                    return _dir;
                }
            }
        }

        /// <summary>
        /// 该目录下是否有状态文件(判定的唯一依据)。
        /// 不只用 <see cref="File.Exists"/>: 热更程序集里 BCL 可能残缺, 所以再补一条
        /// "能不能真的打开它"的判定 —— 两条都失败才认定没有。
        /// </summary>
        private static bool HasStateFile(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;

            var path = Combine(dir, StateFileName);
            if (string.IsNullOrEmpty(path)) return false;

            try { if (File.Exists(path)) return true; } catch { }

            try
            {
                using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return fs != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// 候选目录(按可信度排序)。不依赖任何单一来源:
        ///   1. CESIUM_SPEED_DIR(加载器注入, 最权威);
        ///   2. CESIUM_LOG_DIR / CESIUM_MODS_DIR / CESIUM_SDK_DIR 的兄弟目录 ../speed
        ///      (只要其中一个环境变量可读就能定位);
        ///   3. 当前目录 / Unity 数据目录推导出的游戏目录下的 AstralParty_ModLoader\speed;
        ///   4. %LOCALAPPDATA%\AstralParty_ModLoader\speed(加载器也会镜像写一份)。
        ///
        /// ⚠️ 热更程序集(HybridCLR/IL2CPP)里 BCL 是**残缺**的: 例如
        /// <c>AppDomain.CurrentDomain.BaseDirectory</c> 会抛
        /// <c>MissingMethodException</c>。所以这里每个来源都单独 try/catch,
        /// 绝不因为"某一条路不通"就把整份候选列表丢掉(踩过这个坑, 见 Diagnostics)。
        /// </summary>
        internal static List<string> Candidates()
        {
            var list = new List<string>();

            // 0. 测试注入的覆盖目录(仅测试用, 游戏里恒为 null)
            Add(list, DirectoryOverride);

            Add(list, EnvOrNull("CESIUM_SPEED_DIR"));

            Add(list, SiblingOf(EnvOrNull("CESIUM_LOG_DIR")));
            Add(list, SiblingOf(EnvOrNull("CESIUM_MODS_DIR")));
            Add(list, SiblingOf(EnvOrNull("CESIUM_SDK_DIR")));

            // 游戏目录推导: <游戏 exe 目录>\AstralParty_ModLoader\speed
            foreach (var baseDir in GameDirCandidates())
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                Add(list, Combine(baseDir, "AstralParty_ModLoader\\speed"));
                // 某些布局下 exe 在子目录, 加载器在上层
                var parent = ParentOf(baseDir);
                if (!string.IsNullOrEmpty(parent))
                    Add(list, Combine(parent, "AstralParty_ModLoader\\speed"));
            }

            // 约定位置(加载器的镜像目录): 先用 LOCALAPPDATA 环境变量, 再退回 BCL 的已知文件夹
            var local = EnvOrNull("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local))
                Add(list, Combine(local, "AstralParty_ModLoader\\speed"));

            Add(list, Combine(KnownFolder(), "AstralParty_ModLoader\\speed"));

            return list;
        }

        private static void Add(List<string> list, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            foreach (var existing in list)
                if (string.Equals(existing, dir, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(dir);
        }

        private static string EnvOrNull(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch { return null; }
        }

        /// <summary>Environment.GetFolderPath 的安全包装(IL2CPP 下可能缺失)。</summary>
        private static string KnownFolder()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
            catch { return null; }
        }

        /// <summary>Path.Combine 的安全包装(Path 不可用时退化成字符串拼接)。</summary>
        internal static string Combine(string dir, string relative)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try { return Path.Combine(dir, relative); }
            catch { }

            // 极端情况: Path 不可用 -> 手工拼(Win32 分隔符, 加载器就在 Windows 上)
            char sep = '\\';
            var trimmed = dir.TrimEnd('\\', '/');
            var rest = relative.TrimStart('\\', '/').Replace('/', sep);
            return trimmed + sep + rest;
        }

        private static string ParentOf(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                var parent = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(parent) ? null : parent;
            }
            catch
            {
                // Path 不可用: 手工截最后一个分隔符
                var trimmed = dir.TrimEnd('\\', '/');
                int i = trimmed.LastIndexOfAny(new[] { '\\', '/' });
                return i > 2 ? trimmed.Substring(0, i) : null;
            }
        }

        /// <summary>"&lt;dir&gt;\..\speed": 从日志/mods/sdk 目录推控制目录。</summary>
        private static string SiblingOf(string dir)
        {
            var parent = ParentOf(dir);
            return string.IsNullOrEmpty(parent) ? null : Combine(parent, "speed");
        }

        /// <summary>
        /// 可能的"游戏根目录"。注意<b>不要</b>用 AppDomain.BaseDirectory ——
        /// HybridCLR 下它是缺失方法(抛 MissingMethodException)。
        /// </summary>
        private static List<string> GameDirCandidates()
        {
            var list = new List<string>();

            try
            {
                var cwd = Environment.CurrentDirectory;
                if (!string.IsNullOrEmpty(cwd)) list.Add(cwd.TrimEnd('\\', '/'));
            }
            catch { }

            try
            {
                // Application.dataPath = <游戏>\<产品>_Data, 父目录就是游戏目录
                // (走 UnityCall 的安全包装: 拿不到 Unity 时返回 null, 不会抛)
                var dataPath = UnityCall.DataPath();
                if (!string.IsNullOrEmpty(dataPath))
                {
                    var parent = ParentOf(dataPath);
                    if (!string.IsNullOrEmpty(parent)) list.Add(parent);
                }
            }
            catch { }

            return list;
        }

        private static string ResolveDirectory()
        {
            // 测试注入的覆盖目录是绝对覆盖: 不做"有没有 state.txt"的判定,
            // 这样用例与运行环境里真实的环境变量完全无关。
            if (!string.IsNullOrEmpty(DirectoryOverride)) return DirectoryOverride;

            // 先单独试最权威的一条(加载器直接注入的目录): 即使后面所有路径推导都不可用,
            // 这一条也必须是独立的 —— 踩过"一个 MissingMethodException 拖垮整份候选列表"的坑。
            var direct = EnvOrNull("CESIUM_SPEED_DIR");
            if (HasStateFile(direct)) return direct;

            var candidates = Candidates();

            // 唯一可靠的判定: 目录里有 state.txt(加载器写出来的)。
            foreach (var candidate in candidates)
                if (HasStateFile(candidate)) return candidate;

            // 都没命中: 返回最可信的一个路径, 只是为了给出可读的报错; 下次调用会重新扫描。
            if (!string.IsNullOrEmpty(direct)) return direct;
            return candidates.Count > 0 ? candidates[0] : null;
        }

        /// <summary>
        /// 候选目录诊断报告(排查"按热键没反应"用): 每个候选 + 是否存在 state.txt,
        /// 以及环境变量原始值 / 状态文件读取失败的具体步骤。写进日志后一眼就能看出卡在哪一环。
        /// 本方法**保证不抛异常**(它在"出问题"时才被调用, 自己再炸就没法排查了)。
        /// </summary>
        public static string Diagnostics()
        {
            var sb = new StringBuilder();
            sb.Append("控制文件诊断: state=").Append(StateFileName);
            sb.Append(" CESIUM_SPEED_DIR='").Append(EnvOrNull("CESIUM_SPEED_DIR") ?? "(null)");
            sb.Append("' CESIUM_LOG_DIR='").Append(EnvOrNull("CESIUM_LOG_DIR") ?? "(null)");
            sb.Append("' CESIUM_MODS_DIR='").Append(EnvOrNull("CESIUM_MODS_DIR") ?? "(null)").Append('\'');

            try
            {
                var candidates = Candidates();
                sb.Append(" 候选 ").Append(candidates.Count).Append(" 个:");
                for (int i = 0; i < candidates.Count; i++)
                {
                    sb.Append(' ').Append(i + 1).Append(')').Append(candidates[i]);
                    bool has;
                    try { has = HasStateFile(candidates[i]); }
                    catch (Exception e) { sb.Append("[探测异常:").Append(e.GetType().Name).Append(']'); continue; }
                    sb.Append(has ? "[有state]" : "[无state]");
                }
            }
            catch (Exception e)
            {
                sb.Append(" 候选枚举失败: ").Append(e.GetType().Name).Append(' ').Append(e.Message);
            }

            if (_stateError != null) sb.Append(" 读取失败于: ").Append(_stateError);
            if (_writeError != null) sb.Append(" 上次写请求失败: ").Append(_writeError);

            try { sb.Append(" 选中=").Append(Directory ?? "(null)"); }
            catch (Exception e) { sb.Append(" 选中=解析异常(").Append(e.GetType().Name).Append(')'); }

            return sb.ToString();
        }

        /// <summary>
        /// 变速引擎是否可用(加载器已装 hook 且控制文件通道可达)。
        /// <b>每次都重新判定</b>(代价只是读一个小文件): 不缓存 false, 因为加载器写 state.txt
        /// 与 mod 初始化之间存在先后差异, 而 mod 的静态状态在整个进程内一直存在 ——
        /// 早期一次误判会永久锁死热键(这正是"按了没反应"最容易踩的坑)。
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                try
                {
                    lock (_lock)
                    {
                        var state = ReadState();
                        if (state == null)
                        {
                            _availabilityChecked = true;
                            _unavailableReason = "读不到加载器状态文件 " +
                                (StateFileName) + "(需要加载器 ≥ 2.1.5 且 speedControlEnabled 不为 false); " +
                                Diagnostics();
                            return false;
                        }

                        int hooks;
                        if (!state.TryGetValue("hooks", out var hooksText) || !TryParseInt(hooksText, out hooks))
                        {
                            _availabilityChecked = true;
                            _unavailableReason = "状态文件缺少/无法解析 hooks 字段(" + Directory + ")";
                            return false;
                        }

                        if (hooks <= 0)
                        {
                            _availabilityChecked = true;
                            _unavailableReason = "加载器安装了 0 个时间 hook(变速引擎不可用)";
                            return false;
                        }

                        _availabilityChecked = true;
                        _unavailableReason = null;
                        return true;
                    }
                }
                catch (Exception e)
                {
                    // 兜底: 本类对外承诺"任何 API 错误都记下来并返回默认值, 不让游戏崩溃"
                    _availabilityChecked = true;
                    _unavailableReason = "可用性判定异常 " + Describe(e) + "; " + Diagnostics();
                    return false;
                }
            }
        }

        /// <summary>
        /// 清掉目录/可用性缓存, 下次访问重新探测(加载器后启动、或用户刚改了配置时可用)。
        /// </summary>
        public static void Refresh()
        {
            ResetCache();
        }

        /// <summary>引擎不可用时的原因(可用时为 null)。</summary>
        public static string UnavailableReason
        {
            get { lock (_lock) { if (!_availabilityChecked) { var _ = IsAvailable; } return _unavailableReason; } }
        }

        /// <summary>当前倍率(读取状态文件; 引擎不可用时恒为 1.0)。</summary>
        public static double Speed
        {
            get { return ReadSpeedValue("speed"); }
        }

        /// <summary>加载器记录的基础倍率(doorstop_config.json 的 speedhackBaseSpeed; 未知为 1.0)。</summary>
        public static double BaseSpeed
        {
            get { return ReadSpeedValue("base"); }
        }

        /// <summary>
        /// 从状态文件读一个倍率字段; 任何异常/非法值都返回 1.0。
        /// 低于 <see cref="MinSpeed"/>(1.0)的值一律当 1.0 —— 即使状态文件是旧加载器/外物写的,
        /// 也不能让 mod 以为当前处于"减速"状态(不允许减速)。
        /// </summary>
        private static double ReadSpeedValue(string field)
        {
            try
            {
                var state = ReadState();
                double value;
                if (state != null && state.TryGetValue(field, out var text) &&
                    TryParseDecimal(text, out value) && value >= MinSpeed && value <= HardMaxSpeed)
                    return value;
            }
            catch { }
            return 1.0;
        }

        /// <summary>
        /// 请求倍率(写控制文件, 由加载器在 ~100ms 内应用)。
        /// </summary>
        /// <param name="speed">倍率, 范围 [<see cref="MinSpeed"/>, <see cref="HardMaxSpeed"/>] = [1, 100],
        /// 1.0 = 正常, 2.0 = 2 倍速。<b>低于 1 倍一律拒绝</b>(返回 false, 不写文件)。</param>
        /// <returns>
        /// 是否成功**发出请求**(倍率非法、引擎不可用、磁盘写入失败时 false; 不抛异常)。
        /// 返回 true 只代表请求已写入, 真正生效由加载器在 100ms 内完成 —— 可用
        /// <see cref="Speed"/> 回读校验。
        /// </returns>
        public static bool SetSpeed(double speed)
        {
            // NaN 必须显式挡掉: 它和任何数比较都是 false, 光靠区间判断拦不住它,
            // 而把 NaN 传进引擎会让虚拟时间变成 NaN(游戏时间彻底坏掉)。
            if (double.IsNaN(speed) || double.IsInfinity(speed)) return false;

            // 硬下限 1.0: 禁止减速(加载器侧还会再拦一次, 这里是让 mod 立刻拿到明确的 false)
            if (speed < MinSpeed || speed > HardMaxSpeed) return false;

            if (!Permissions.Require(ModPermission.SpeedHack, "SpeedHack.SetSpeed")) return false;
            if (!IsAvailable) return false;

            string dir = Directory;
            if (string.IsNullOrEmpty(dir)) { _writeError = "控制目录未解析"; return false; }

            try { System.IO.Directory.CreateDirectory(dir); }
            catch (Exception e) { _writeError = "建目录失败 " + Describe(e); return false; }

            var path = Combine(dir, RequestFileName);
            if (string.IsNullOrEmpty(path)) { _writeError = "路径拼接失败"; return false; }

            string error;
            var text = speed.ToString(SpeedFormat, CultureInfo.InvariantCulture) + "\r\n";
            if (!WriteAtomic(path, text, out error)) { _writeError = error; return false; }

            _writeError = null;
            return true;
        }

        /// <summary>
        /// 恢复正常速度(倍率 1.0)。注意: 这只是把倍率写成 1.0, <b>不</b>会卸载 hook、
        /// 也不改变引擎的激活状态(见类注释里对 1.0 语义的说明)。
        /// </summary>
        public static bool Reset() => SetSpeed(1.0);

        /// <summary>诊断描述(写进日志用)。</summary>
        public static string Describe()
        {
            if (!IsAvailable)
                return "变速引擎不可用(" + (UnavailableReason ?? "原因未知") + ")";
            return "变速引擎可用 当前 " +
                   Speed.ToString("0.###", CultureInfo.InvariantCulture) + "x 基础 " +
                   BaseSpeed.ToString("0.###", CultureInfo.InvariantCulture) + "x 目录 " + Directory;
        }

        // ---------- 状态文件读取 ----------

        /// <summary>
        /// 读状态文件。**只用最原始的 API**(File.Exists/File.Open/FileStream.Read + 手写解析),
        /// 因为热更程序集(HybridCLR/IL2CPP)里 BCL 是残缺的: 带 Encoding 的重载、File.ReadAllLines、
        /// AppDomain 之类都可能直接抛异常。任何失败都会把**具体是哪一步、什么异常**记进
        /// <see cref="_stateError"/>, 由 Diagnostics() 打在日志里 —— 不再出现"原因不明"。
        /// </summary>
        private static Dictionary<string, string> ReadState()
        {
            _stateError = null;

            string dir;
            try { dir = Directory; }
            catch (Exception e) { _stateError = "解析目录异常 " + Describe(e); return null; }

            if (string.IsNullOrEmpty(dir)) { _stateError = "控制目录未解析"; return null; }

            var path = Combine(dir, StateFileName);
            if (string.IsNullOrEmpty(path)) { _stateError = "路径拼接失败"; return null; }

            byte[] bytes;
            try
            {
                if (!HasStateFile(dir)) { _stateError = "文件不存在或打不开: " + path; return null; }

                using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var length = (int)fs.Length;
                    if (length <= 0) { _stateError = "文件为空: " + path; return null; }
                    if (length > 64 * 1024) { _stateError = "文件异常大(" + length + " 字节)"; return null; }

                    bytes = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int n = fs.Read(bytes, read, length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read <= 0) { _stateError = "读出 0 字节"; return null; }
                    if (read < length) bytes = TrimTo(bytes, read);
                }
            }
            catch (Exception e) { _stateError = "读取异常 " + Describe(e); return null; }

            var map = ParseKeyValues(Ascii(bytes));
            if (map.Count == 0) { _stateError = "内容无法解析"; return null; }
            return map;
        }

        private static byte[] TrimTo(byte[] bytes, int length)
        {
            var copy = new byte[length];
            for (int i = 0; i < length; i++) copy[i] = bytes[i];
            return copy;
        }

        /// <summary>状态文件是纯 ASCII(key=value), 手工解码以免依赖 Encoding。</summary>
        private static string Ascii(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }

        /// <summary>请求/状态文本 -> ASCII 字节(内容只有数字/点/换行, 不需要任何编码器)。</summary>
        private static byte[] AsciiBytes(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)(text[i] & 0x7F);
            return bytes;
        }

        /// <summary>手写 key=value 解析(不依赖 Split/Encoding 等可能缺失的重载)。</summary>
        private static Dictionary<string, string> ParseKeyValues(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            int i = 0, n = text.Length;
            while (i < n)
            {
                int lineEnd = text.IndexOf('\n', i);
                if (lineEnd < 0) lineEnd = n;

                int eq = text.IndexOf('=', i);
                if (eq >= 0 && eq < lineEnd)
                {
                    string key = text.Substring(i, eq - i).Trim();
                    string value = text.Substring(eq + 1, lineEnd - eq - 1).Trim();
                    if (key.Length > 0) map[key] = value;
                }
                i = lineEnd + 1;
            }
            return map;
        }

        private static string Describe(Exception e)
        {
            try { return e.GetType().Name + ": " + e.Message; }
            catch { return "未知异常"; }
        }

        // ---------- 手写数值解析 ----------
        // 状态文件是加载器(C++)用固定格式写出来的纯 ASCII 十进制, 所以这里完全不需要
        // double.TryParse(text, NumberStyles, CultureInfo, out) 这类"带重载的 BCL 方法" ——
        // 它们在热更程序集里同样可能整条缺失(和 AppDomain.BaseDirectory 一个道理)。
        // 手写解析只用到 string 索引与算术, 是这里最安全的做法。

        private static bool TryParseDecimal(string text, out double value)
        {
            value = 0.0;
            if (string.IsNullOrEmpty(text)) return false;

            int i = 0, n = text.Length;
            while (i < n && (text[i] == ' ' || text[i] == '\t')) i++;

            bool negative = false;
            if (i < n && (text[i] == '+' || text[i] == '-')) { negative = text[i] == '-'; i++; }

            long integer = 0;
            int digits = 0;
            while (i < n && text[i] >= '0' && text[i] <= '9')
            {
                integer = integer * 10 + (text[i] - '0');
                i++;
                digits++;
                if (integer > 1000000) return false;   // 倍率范围远小于此, 超出就是垃圾数据
            }

            double fraction = 0.0, scale = 1.0;
            if (i < n && text[i] == '.')
            {
                i++;
                while (i < n && text[i] >= '0' && text[i] <= '9')
                {
                    fraction = fraction * 10.0 + (text[i] - '0');
                    scale *= 10.0;
                    i++;
                    digits++;
                    if (scale > 1e9) return false;
                }
            }

            while (i < n && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r')) i++;
            if (digits == 0 || i != n) return false;

            double result = integer + fraction / scale;
            if (negative) result = -result;
            if (double.IsNaN(result) || double.IsInfinity(result)) return false;

            value = result;
            return true;
        }

        private static bool TryParseInt(string text, out int value)
        {
            value = 0;
            double parsed;
            if (!TryParseDecimal(text, out parsed)) return false;
            if (parsed < 0.0 || parsed > 1000000.0) return false;
            value = (int)parsed;
            return true;
        }

        /// <summary>
        /// 写请求文件。同样只用原始 API: 先写 .tmp, 再用 Copy(覆盖) 顶替 ——
        /// 既不依赖 File.Replace / 带 Encoding 的重载, 也不会出现"目标文件短暂消失"的窗口。
        /// </summary>
        private static bool WriteAtomic(string path, string text, out string error)
        {
            error = null;
            string tmp = path + ".tmp";
            var bytes = AsciiBytes(text);

            try
            {
                using (var fs = File.Open(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                }
            }
            catch (Exception e) { error = "写临时文件失败 " + Describe(e); return false; }

            try
            {
                if (File.Exists(path)) File.Copy(tmp, path, true);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { error = "替换请求文件失败 " + Describe(e); return false; }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>
        /// 把倍率夹到 [<paramref name="min"/>, <paramref name="max"/>] 并规整小数位。
        /// 非法输入(NaN/Inf)回归 1.0; min/max 写反了自动交换。
        ///
        /// <b>下限永远是 <see cref="MinSpeed"/>(1.0)</b>: 即使调用方传了更小的 min
        /// (例如 config.json 里手改成 0.2)也会被抬到 1.0 —— "不允许减速"是硬规则。
        /// </summary>
        public static double ClampSpeed(double speed, double min = MinSpeed, double max = MaxSpeed)
        {
            if (double.IsNaN(min) || double.IsInfinity(min) || min <= 0.0) min = MinSpeed;
            if (double.IsNaN(max) || double.IsInfinity(max) || max <= 0.0) max = MaxSpeed;
            if (min > max) { double tmp = min; min = max; max = tmp; }

            if (min < MinSpeed) min = MinSpeed;   // 硬下限: 任何调用方都抬不过去
            if (max < MinSpeed) max = MinSpeed;

            if (double.IsNaN(speed) || double.IsInfinity(speed)) return MinSpeed;
            if (speed < min) speed = min;
            if (speed > max) speed = max;

            // 0.5 这类步进累加会飘成 2.5000000000000004, 规整到 3 位小数
            return Math.Round(speed, 3, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 纯计算(不碰加载器, 可离线测试): 按一次热键把倍率朝 <paramref name="delta"/> 方向调整
        /// <paramref name="step"/>, 结果按 [<paramref name="min"/>, <paramref name="max"/>] 夹紧
        /// (下限依然是硬性的 <see cref="MinSpeed"/> = 1.0, 传更小的 min 也会被抬上去)。
        ///
        /// 容错: <paramref name="step"/> ≤ 0 / NaN / Inf 时退回 <see cref="DefaultSpeedStep"/>;
        /// <paramref name="min"/>/<paramref name="max"/> 非法时退回 <see cref="MinSpeed"/>/<see cref="MaxSpeed"/>。
        /// 返回值与 <paramref name="current"/> 相同即表示已到极限(调用方据此提示, 不必写盘) ——
        /// 在 1.0x 继续按"减速"就会得到这个结果。
        /// </summary>
        public static double StepSpeed(double current, double delta, double step = DefaultSpeedStep,
            double min = MinSpeed, double max = MaxSpeed)
        {
            if (double.IsNaN(current) || double.IsInfinity(current)) current = MinSpeed;
            if (double.IsNaN(delta) || double.IsInfinity(delta) || delta == 0.0)
                return ClampSpeed(current, min, max);
            if (double.IsNaN(step) || double.IsInfinity(step) || step <= 0.0) step = DefaultSpeedStep;
            return ClampSpeed(current + delta * step, min, max);
        }
    }
}
