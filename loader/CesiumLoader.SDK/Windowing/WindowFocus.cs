using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CesiumLoader.SDK.Windowing
{
    /// <summary>
    /// 游戏窗口置前 (把游戏窗口切到前台)。
    ///
    /// 热更程序集(经 HybridCLR 加载)在运行时无法 P/Invoke, 任何 <c>[DllImport]</c>
    /// 都会抛 <c>NotSupportManaged2NativeFunctionMethod</c>, 所以不能直接调
    /// <c>user32!SetForegroundWindow</c>。本类把请求发给同进程的原生加载器,
    /// 由它执行:
    ///
    /// <code>
    ///   127.0.0.1:&lt;CESIUM_FOCUS_PORT&gt;  mod → 加载器(UDP datagram): 十进制令牌 + 可选提示音绝对路径
    /// </code>
    ///
    /// 端口由加载器在 127.0.0.1 上动态分配(bind 0), 并写入同进程的环境变量
    /// CESIUM_FOCUS_PORT——每进程一个端口, 多开时请求只会送到自己的进程,
    /// 不存在旧版文件通道那种共享 request.txt 的串号问题。
    /// (与 <c>NativeAudioBackend</c> 同款模式: System.Net.Sockets.Socket + 环境变量发现。)
    ///
    /// 需要加载器提供窗口置前通道; 加载器未提供时 <see cref="IsAvailable"/> 为 <c>false</c>。
    ///
    /// 提示音(<see cref="RequestForeground(string)"/>)由加载器原生播放, 不经过游戏音频;
    /// UDP 通道一经就绪即支持 WAV(见 <see cref="SupportsSound"/>)。
    ///
    /// 注意: Windows 的前台锁定可能拒绝置前(例如前台是全屏游戏或提权窗口),
    /// 此时加载器会闪烁任务栏提醒, 无法保证 100% 抢到前台。
    /// </summary>
    public static class WindowFocus
    {
        private static readonly object _lock = new object();
        private static long _sequence;
        private static string _unavailableReason;
        private static Socket _socket;
        private static EndPoint _endPoint;
        private static int _cachedPort;

        // 测试用: 覆盖 CESIUM_FOCUS_PORT / 清缓存(避免依赖进程环境变量与真实加载器)。
        internal static int PortOverride { get; set; }

        // 保留供旧测试/诊断兼容: UDP 模式下不再使用文件目录, 该属性仅用于不让
        // 依赖它的外部代码编译失败。真正的传输地址见 Describe() / CESIUM_FOCUS_PORT。
        [Obsolete("窗口通道已改用 UDP(CESIUM_FOCUS_PORT), 不再依赖文件目录。")]
        internal static string DirectoryOverride { get; set; }

        internal static void ResetCache()
        {
            lock (_lock)
            {
                if (_socket != null)
                {
                    try { _socket.Close(); } catch { }
                    _socket = null;
                }
                _endPoint = null;
                _cachedPort = 0;
                _unavailableReason = null;
            }
        }

        /// <summary>
        /// 遗留属性: UDP 模式不再有控制文件目录。这里回环境变量 CESIUM_WINDOW_DIR
        /// 的值(仅用于诊断展示), 可能为空——传输地址看 <see cref="Describe"/>。
        /// </summary>
        public static string Directory
        {
            get { return EnvOrNull("CESIUM_WINDOW_DIR"); }
        }

        /// <summary>加载器是否提供窗口置前通道(同进程 CESIUM_FOCUS_PORT 存在且有效)。</summary>
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
        /// 是否成功**发出请求**(通道不可用、UDP 发送失败时 false; 不抛异常)。
        /// 返回 true 只代表 datagram 已发往 127.0.0.1:&lt;port&gt;, 真正置前由加载器
        /// 收到后执行; 游戏窗口已经在前台时请求本身仍会成功, 但不会改变任何窗口状态。
        /// </returns>
        public static bool RequestForeground()
        {
            return RequestForeground(null);
        }

        /// <summary>
        /// 请求加载器把游戏窗口切换到前台, 并在置前时播放提示音。
        ///
        /// 提示音由加载器用原生 <c>PlaySound</c> 播放, 不经过游戏的 Wwise 音频,
        /// 因此游戏内音量/静音不影响它。加载器只播放 **WAV**(建议 16-bit PCM)。
        ///
        /// 默认只在游戏窗口**不在前台**时播放(此时置前才是真正需要的提醒);
        /// 需要"每次请求都响, 不管前台"时用 <see cref="RequestForeground(string, bool)"/>。
        /// </summary>
        /// <param name="soundFile">
        /// 提示音的**绝对路径**。传 null/空字符串等于只置前;
        /// 相对路径、含换行/回车的路径会被忽略(结果同样是只置前)。
        /// 文件不存在、扩展名不是 .wav 或超过 16 MB 时加载器只记日志, 置前不受影响。
        /// </param>
        /// <returns>同 <see cref="RequestForeground()"/>。</returns>
        public static bool RequestForeground(string soundFile)
        {
            return RequestForeground(soundFile, false);
        }

        /// <summary>
        /// 请求置前并播放提示音, 可指定"窗口已在前台时也播放"。
        /// </summary>
        /// <param name="soundFile">提示音绝对路径(WAV), 规则同上。</param>
        /// <param name="alwaysPlaySound">
        /// <c>true</c> = 每次新请求都播放, 不管游戏窗口在不在前台;
        /// <c>false</c> = 只在窗口不在前台时播放(默认)。没有提示音时该参数无意义。
        /// </param>
        /// <returns>同 <see cref="RequestForeground()"/>。</returns>
        public static bool RequestForeground(string soundFile, bool alwaysPlaySound)
        {
            lock (_lock)
            {
                if (!IsAvailableLocked()) return false;

                int port = _cachedPort;
                if (port <= 0) { _unavailableReason = "UDP 端口未解析(CESIUM_FOCUS_PORT)"; return false; }

                // 令牌只需与上一次不同: 保留与旧文件版一致的报文体, 加载器沿用同一套解析。
                long token;
                try { token = DateTime.UtcNow.Ticks + (++_sequence); }
                catch { token = ++_sequence; }

                var text = BuildRequestText(token, soundFile, alwaysPlaySound);
                var bytes = Utf8Bytes(text);

                try
                {
                    if (_socket == null)
                    {
                        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    }
                    _socket.SendTo(bytes, _endPoint);
                }
                catch (Exception e)
                {
                    _unavailableReason = "UDP 发送失败 " + Describe(e);
                    return false;
                }

                _unavailableReason = null;
                return true;
            }
        }

        /// <summary>加载器是否提供提示音(UDP 通道就绪即支持 WAV; 旧版加载器无端口时为 false)。</summary>
        public static bool SupportsSound
        {
            get
            {
                lock (_lock)
                {
                    return IsAvailableLocked();
                }
            }
        }

        /// <summary>请求报文: 第一行令牌, 第二行可选提示音路径, 第三行可选 always=1。</summary>
        internal static string BuildRequestText(long token, string soundFile, bool alwaysPlaySound = false)
        {
            var sb = new StringBuilder();
            sb.Append(token.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

            string path = NormalizeSoundPath(soundFile);
            if (path != null)
            {
                sb.Append(path).Append("\r\n");
                if (alwaysPlaySound) sb.Append("always=1\r\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 只接受绝对路径的单行文本: 相对路径会按游戏工作目录解析(行为不确定),
        /// 换行会破坏请求报文的行结构, 两者都直接丢弃。
        /// </summary>
        internal static string NormalizeSoundPath(string soundFile)
        {
            if (soundFile == null) return null;

            string path = soundFile.Trim();
            if (path.Length == 0 || path.Length > 4096) return null;
            if (path.IndexOf('\r') >= 0 || path.IndexOf('\n') >= 0) return null;
            if (!IsAbsolutePath(path)) return null;

            return path;
        }

        /// <summary>
        /// 绝对路径判定, 与加载器的解析规则一致: 驱动器 + 分隔符("C:\..."、"C:/..."),
        /// 或 UNC("\\server\...")。不用 <c>Path.IsPathRooted</c> —— 热更侧没验证过这个成员。
        /// </summary>
        internal static bool IsAbsolutePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            if (path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) return true;
            if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\') return true;

            return false;
        }

        /// <summary>诊断描述(写进日志用)。</summary>
        public static string Describe()
        {
            lock (_lock)
            {
                bool available = IsAvailableLocked();
                var sb = new StringBuilder();
                sb.Append(available ? "窗口置前通道可用" : "窗口置前通道不可用");
                sb.Append(" 端点 ").Append(available ? "127.0.0.1:" + _cachedPort : "(无)");
                sb.Append(SupportsSoundLockedSilent() ? " 提示音=wav" : " 提示音=无");
                if (!available && !string.IsNullOrEmpty(_unavailableReason))
                    sb.Append(" 原因: ").Append(_unavailableReason);
                return sb.ToString();
            }
        }

        // ---------- 可用性 / 端口发现 ----------

        private static bool SupportsSoundLockedSilent()
        {
            return _cachedPort > 0;
        }

        private static bool IsAvailableLocked()
        {
            int port = ResolvePortLocked();
            if (port <= 0)
            {
                _unavailableReason = "未找到 CESIUM_FOCUS_PORT(需要加载器提供窗口置前 UDP 通道, 且 windowControlEnabled 不为 false)";
                _cachedPort = 0;
                _endPoint = null;
                return false;
            }

            _cachedPort = port;
            _endPoint = new IPEndPoint(IPAddress.Loopback, port);
            _unavailableReason = null;
            return true;
        }

        private static int ResolvePortLocked()
        {
            if (PortOverride > 0) return PortOverride;

            string env = EnvOrNull("CESIUM_FOCUS_PORT");
            if (!string.IsNullOrEmpty(env))
            {
                string trimmed = env.Trim();
                int p;
                if (int.TryParse(trimmed, out p) && p > 0 && p <= 65535)
                    return p;
            }

            // 缓存的端口在环境变量消失时(如测试切换)应失效: 每次都重新解析, 可靠优先。
            return 0;
        }

        private static string EnvOrNull(string name)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch { return null; }
        }

        // ---------- 原始编码(与旧文件版一致) ----------

        /// <summary>
        /// 手写 UTF-8 编码: 热更 BCL 的 <c>System.Text.Encoding</c> 在本项目里没有实测过,
        /// 而提示音路径可能是中文, 不能沿用原来"按 ASCII 位与"的写法。
        /// </summary>
        internal static byte[] Utf8Bytes(string text)
        {
            var bytes = new List<byte>(text.Length + 4);

            for (int i = 0; i < text.Length; i++)
            {
                int code = text[i];

                if (code >= 0xD800 && code <= 0xDBFF)
                {
                    bool paired = false;
                    if (i + 1 < text.Length)
                    {
                        int low = text[i + 1];
                        if (low >= 0xDC00 && low <= 0xDFFF)
                        {
                            code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                            i++;
                            paired = true;
                        }
                    }
                    if (!paired) code = 0xFFFD;   // 落单的高代理
                }
                else if (code >= 0xDC00 && code <= 0xDFFF)
                {
                    code = 0xFFFD;                // 落单的低代理
                }

                if (code < 0x80)
                {
                    bytes.Add((byte)code);
                }
                else if (code < 0x800)
                {
                    bytes.Add((byte)(0xC0 | (code >> 6)));
                    bytes.Add((byte)(0x80 | (code & 0x3F)));
                }
                else if (code < 0x10000)
                {
                    bytes.Add((byte)(0xE0 | (code >> 12)));
                    bytes.Add((byte)(0x80 | ((code >> 6) & 0x3F)));
                    bytes.Add((byte)(0x80 | (code & 0x3F)));
                }
                else
                {
                    bytes.Add((byte)(0xF0 | (code >> 18)));
                    bytes.Add((byte)(0x80 | ((code >> 12) & 0x3F)));
                    bytes.Add((byte)(0x80 | ((code >> 6) & 0x3F)));
                    bytes.Add((byte)(0x80 | (code & 0x3F)));
                }
            }

            return bytes.ToArray();
        }

        private static string Describe(Exception e)
        {
            try { return e.GetType().Name + ": " + e.Message; }
            catch { return "未知异常"; }
        }
    }
}
