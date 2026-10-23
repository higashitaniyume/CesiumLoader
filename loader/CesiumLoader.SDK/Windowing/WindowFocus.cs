using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CesiumLoader.SDK.Windowing
{
    /// <summary>
    /// 游戏窗口置前 (把游戏窗口切到前台)。
    ///
    /// 与 <see cref="CesiumLoader.SDK.Speed.SpeedHack"/> 一样走**加载器控制文件通道**:
    /// 热更程序集(经 HybridCLR 加载)在运行时无法 P/Invoke, 任何 <c>[DllImport]</c>
    /// 都会抛 <c>NotSupportManaged2NativeFunctionMethod</c>, 所以不能直接调
    /// <c>user32!SetForegroundWindow</c>。本类只写一个请求文件, 由原生加载器执行:
    ///
    /// <code>
    ///   &lt;CESIUM_WINDOW_DIR&gt;\request.txt   mod → 加载器: 每次请求写一个新的十进制令牌
    ///   &lt;CESIUM_WINDOW_DIR&gt;\state.txt     加载器 → mod: version / available
    /// </code>
    ///
    /// 加载器每 100ms 检查一次请求文件; 游戏窗口已经在前台时静默忽略, 不抢焦点。
    /// 需要加载器提供窗口置前通道; 加载器未提供时 <see cref="IsAvailable"/> 为 <c>false</c>。
    ///
    /// 注意: Windows 的前台锁定可能拒绝置前(例如前台是全屏游戏或提权窗口),
    /// 此时加载器会闪烁任务栏提醒, 无法保证 100% 抢到前台。
    /// </summary>
    public static class WindowFocus
    {
        private const string RequestFileName = "request.txt";
        private const string StateFileName = "state.txt";

        private static readonly object _lock = new object();
        private static string _dir;
        private static long _sequence;
        private static string _unavailableReason;

        // 测试用: 覆盖控制文件目录 / 清缓存(避免依赖进程环境变量与真实游戏目录)。
        internal static string DirectoryOverride { get; set; }

        internal static void ResetCache()
        {
            lock (_lock)
            {
                _dir = null;
                _unavailableReason = null;
            }
        }

        /// <summary>
        /// 控制文件目录。按候选顺序找第一个**真的存在 state.txt** 的目录;
        /// 找不到时返回第一个非空候选(供报错用)。结果会被缓存, 但每次读取都会验证
        /// 缓存目录里 state.txt 还在 —— 否则重扫。
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

        /// <summary>加载器是否提供窗口置前通道(读 state.txt 的 available=1)。</summary>
        public static bool IsAvailable
        {
            get
            {
                lock (_lock)
                {
                    return IsAvailableLocked();
                }
            }
        }

        /// <summary>通道不可用时的原因(可用时为 null)。</summary>
        public static string UnavailableReason
        {
            get
            {
                lock (_lock)
                {
                    if (_unavailableReason == null) IsAvailableLocked();
                    return _unavailableReason;
                }
            }
        }

        /// <summary>
        /// 请求加载器把游戏窗口切换到前台。
        /// </summary>
        /// <returns>
        /// 是否成功**发出请求**(通道不可用、目录/文件写不进去时 false; 不抛异常)。
        /// 返回 true 只代表请求已落盘, 真正置前由加载器在 100ms 内执行;
        /// 游戏窗口已经在前台时请求本身仍会成功, 但不会改变任何窗口状态。
        /// </returns>
        public static bool RequestForeground()
        {
            lock (_lock)
            {
                if (!IsAvailableLocked()) return false;

                string dir = _dir;
                if (string.IsNullOrEmpty(dir)) { _unavailableReason = "控制目录未解析"; return false; }

                try { System.IO.Directory.CreateDirectory(dir); }
                catch (Exception e) { _unavailableReason = "建目录失败 " + Describe(e); return false; }

                var path = Combine(dir, RequestFileName);
                if (string.IsNullOrEmpty(path)) { _unavailableReason = "路径拼接失败"; return false; }

                // 令牌只需与上一次不同: 加载器只处理"内容变化"。
                long token;
                try { token = DateTime.UtcNow.Ticks + (++_sequence); }
                catch { token = ++_sequence; }

                var text = token.ToString(CultureInfo.InvariantCulture) + "\r\n";
                string error;
                if (!WriteAtomic(path, text, out error))
                {
                    _unavailableReason = error;
                    return false;
                }

                _unavailableReason = null;
                return true;
            }
        }

        /// <summary>诊断描述(写进日志用)。</summary>
        public static string Describe()
        {
            lock (_lock)
            {
                bool available = IsAvailableLocked();
                var sb = new StringBuilder();
                sb.Append(available ? "窗口置前通道可用" : "窗口置前通道不可用");
                sb.Append(" 目录 ").Append(Directory ?? "(null)");
                if (!available && !string.IsNullOrEmpty(_unavailableReason))
                    sb.Append(" 原因: ").Append(_unavailableReason);
                return sb.ToString();
            }
        }

        // ---------- 可用性 / 状态文件 ----------

        private static bool IsAvailableLocked()
        {
            var state = ReadState();
            if (state == null)
            {
                _unavailableReason = "读不到加载器状态文件 " + StateFileName +
                    "(需要加载器提供窗口置前通道, 且 windowControlEnabled 不为 false)";
                return false;
            }

            string available;
            if (!state.TryGetValue("available", out available) || available != "1")
            {
                _unavailableReason = "状态文件未标记 available=1(" + Directory + ")";
                return false;
            }

            _unavailableReason = null;
            return true;
        }

        private static Dictionary<string, string> ReadState()
        {
            string dir = Directory;
            if (string.IsNullOrEmpty(dir)) return null;

            var path = Combine(dir, StateFileName);
            if (string.IsNullOrEmpty(path)) return null;

            byte[] bytes;
            try
            {
                if (!HasStateFile(dir)) return null;

                using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var length = (int)fs.Length;
                    if (length <= 0 || length > 64 * 1024) return null;

                    bytes = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int n = fs.Read(bytes, read, length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read <= 0) return null;
                    if (read < length) bytes = TrimTo(bytes, read);
                }
            }
            catch { return null; }

            return ParseKeyValues(Ascii(bytes));
        }

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

        // ---------- 目录解析 ----------

        private static string ResolveDirectory()
        {
            if (!string.IsNullOrEmpty(DirectoryOverride)) return DirectoryOverride;

            // 加载器直接注入的目录最权威; 单独先试, 避免一条异常拖垮整份候选。
            var direct = EnvOrNull("CESIUM_WINDOW_DIR");
            if (HasStateFile(direct)) return direct;

            var candidates = Candidates();
            foreach (var candidate in candidates)
                if (HasStateFile(candidate)) return candidate;

            if (!string.IsNullOrEmpty(direct)) return direct;
            return candidates.Count > 0 ? candidates[0] : null;
        }

        private static List<string> Candidates()
        {
            var list = new List<string>();

            Add(list, DirectoryOverride);
            Add(list, EnvOrNull("CESIUM_WINDOW_DIR"));

            Add(list, SiblingOf(EnvOrNull("CESIUM_LOG_DIR")));
            Add(list, SiblingOf(EnvOrNull("CESIUM_MODS_DIR")));
            Add(list, SiblingOf(EnvOrNull("CESIUM_SDK_DIR")));

            foreach (var baseDir in GameDirCandidates())
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                Add(list, Combine(baseDir, "AstralParty_ModLoader\\window"));
                var parent = ParentOf(baseDir);
                if (!string.IsNullOrEmpty(parent))
                    Add(list, Combine(parent, "AstralParty_ModLoader\\window"));
            }

            var local = EnvOrNull("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local))
                Add(list, Combine(local, "AstralParty_ModLoader\\window"));

            Add(list, Combine(KnownFolder(), "AstralParty_ModLoader\\window"));

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

        private static string KnownFolder()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
            catch { return null; }
        }

        private static string Combine(string dir, string relative)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try { return Path.Combine(dir, relative); }
            catch { }

            var trimmed = dir.TrimEnd('\\', '/');
            var rest = relative.TrimStart('\\', '/').Replace('/', '\\');
            return trimmed + "\\" + rest;
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
                var trimmed = dir.TrimEnd('\\', '/');
                int i = trimmed.LastIndexOfAny(new[] { '\\', '/' });
                return i > 2 ? trimmed.Substring(0, i) : null;
            }
        }

        private static string SiblingOf(string dir)
        {
            var parent = ParentOf(dir);
            return string.IsNullOrEmpty(parent) ? null : Combine(parent, "window");
        }

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
                var dataPath = Internals.UnityCall.DataPath();
                if (!string.IsNullOrEmpty(dataPath))
                {
                    var parent = ParentOf(dataPath);
                    if (!string.IsNullOrEmpty(parent)) list.Add(parent);
                }
            }
            catch { }

            return list;
        }

        // ---------- 原始文件 API(热更 BCL 可能残缺) ----------

        private static byte[] TrimTo(byte[] bytes, int length)
        {
            var copy = new byte[length];
            for (int i = 0; i < length; i++) copy[i] = bytes[i];
            return copy;
        }

        private static string Ascii(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }

        private static byte[] AsciiBytes(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)(text[i] & 0x7F);
            return bytes;
        }

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

        private static string Describe(Exception e)
        {
            try { return e.GetType().Name + ": " + e.Message; }
            catch { return "未知异常"; }
        }
    }
}
