using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SdkPath = CesiumLoader.SDK.IO.Path;

namespace CesiumLoader.SDK.Updates
{
    /// <summary>
    /// 加载器更新检查的**只读**客户端：读原生加载器写好的检查结果，并按需请求重新检查 /
    /// 打开浏览器。不联网 —— HTTPS 由原生层（WinHTTP）负责，热更程序集不碰 TLS。
    ///
    /// 通道（见 CesiumLoader 的 <c>update/update.h</c>）：
    /// <list type="bullet">
    /// <item>状态文件 <c>&lt;loader&gt;\update\state.json</c>：加载器写，这里读；</item>
    /// <item>本地 UDP <c>127.0.0.1:&lt;CESIUM_UPDATE_PORT&gt;</c>：这里发 <c>check</c> /
    ///       <c>open &lt;url&gt;</c>，加载器执行。</item>
    /// </list>
    ///
    /// 线程：<see cref="TryGetState"/> 可在任意线程调用（纯文件读）；其余方法线程安全。
    /// 全部 API 出错只返回 false，不抛异常 —— UI 提示类功能不该把调用方拖垮。
    /// </summary>
    public static class UpdateCheck
    {
        /// <summary>没有可用状态时的兜底发布页（与加载器默认清单同源）。</summary>
        public const string FallbackReleasePage = "https://github.com/higashitaniyume/CesiumLoader/releases/latest";

        private static readonly object _lock = new object();
        private static Socket _socket;
        private static EndPoint _endPoint;
        private static int _cachedPort;
        private static string _unavailableReason;

        // 测试用：覆盖 CESIUM_UPDATE_PORT / 清缓存，避免依赖进程环境变量与真实加载器。
        internal static int PortOverride { get; set; }

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

        // =====================================================================
        // 状态文件
        // =====================================================================

        /// <summary>状态文件完整路径（加载器没写时文件不存在）。</summary>
        public static string StatePath
        {
            get
            {
                try
                {
                    string dir = EnvOrNull("CESIUM_UPDATE_DIR");
                    if (!string.IsNullOrEmpty(dir)) return SdkPath.Combine(dir, "state.json");
                    return SdkPath.GetLoaderPath("update", "state.json");
                }
                catch
                {
                    return "update\\state.json";
                }
            }
        }

        /// <summary>状态目录（<c>&lt;loader&gt;\update</c>）。</summary>
        public static string StateDirectory
        {
            get
            {
                try { return SdkPath.GetDirectoryName(StatePath); }
                catch { return "update"; }
            }
        }

        /// <summary>
        /// 读取最近一次检查结果。文件不存在（加载器没开检查 / 还没写完）时返回 false。
        /// 读到的内容会按字段填进 <paramref name="state"/>；无法解析时同样返回 false。
        /// </summary>
        public static bool TryGetState(out UpdateCheckState state)
        {
            state = null;
            try
            {
                string path = StatePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

                string json = File.ReadAllText(path);
                if (string.IsNullOrEmpty(json)) return false;

                state = Parse(json);
                return state != null;
            }
            catch
            {
                return false;
            }
        }

        // =====================================================================
        // 请求通道（UDP → 原生加载器）
        // =====================================================================

        /// <summary>加载器是否提供了 UDP 通道（<c>CESIUM_UPDATE_PORT</c> 有效）。</summary>
        public static bool IsAvailable
        {
            get { lock (_lock) { return IsAvailableLocked(); } }
        }

        /// <summary>通道不可用时的原因（可用时为 null）。</summary>
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
        /// 请求加载器**忽略冷却**立刻重新检查一次。结果会在几秒后落到状态文件里
        /// （调用方自己轮询 <see cref="TryGetState"/>，这里不等待、不阻塞）。
        /// </summary>
        public static bool RequestCheck()
        {
            return Send("check");
        }

        /// <summary>
        /// 用默认浏览器打开更新发布页。地址取自最近一次检查结果；
        /// 没有结果时退到 <see cref="FallbackReleasePage"/>。
        /// </summary>
        public static bool OpenReleasePage()
        {
            UpdateCheckState state;
            string url = TryGetState(out state) && state != null && !string.IsNullOrEmpty(state.ReleaseUrl)
                ? state.ReleaseUrl
                : FallbackReleasePage;
            return OpenUrl(url);
        }

        /// <summary>
        /// 请求加载器用默认浏览器打开一个 https 地址。
        /// 只接受 https（加载器还只放行 GitHub 与已配置镜像的域名），其他一律 false。
        /// </summary>
        public static bool OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length > 2000) return false;
            if (url.IndexOf('\n') >= 0 || url.IndexOf('\r') >= 0 || url.IndexOf(' ') >= 0) return false;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            return Send("open " + url);
        }

        private static bool Send(string message)
        {
            lock (_lock)
            {
                if (!IsAvailableLocked()) return false;

                int port = _cachedPort;
                if (port <= 0)
                {
                    _unavailableReason = "UDP 端口未解析（CESIUM_UPDATE_PORT）";
                    return false;
                }

                try
                {
                    if (_socket == null)
                    {
                        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    }
                    _socket.SendTo(Encoding.UTF8.GetBytes(message), _endPoint);
                }
                catch (Exception e)
                {
                    _unavailableReason = "UDP 发送失败 " + e.Message;
                    return false;
                }

                _unavailableReason = null;
                return true;
            }
        }

        private static bool IsAvailableLocked()
        {
            if (_endPoint != null && _cachedPort > 0) return true;

            int port = PortOverride;
            if (port <= 0)
            {
                string text = EnvOrNull("CESIUM_UPDATE_PORT");
                if (!string.IsNullOrEmpty(text)) int.TryParse(text, out port);
            }

            if (port <= 0 || port > 65535)
            {
                _unavailableReason = "CESIUM_UPDATE_PORT 未设置或无效（加载器过旧或更新检查未启动）";
                return false;
            }

            try
            {
                _cachedPort = port;
                _endPoint = new IPEndPoint(IPAddress.Loopback, port);
                _unavailableReason = null;
                return true;
            }
            catch (Exception e)
            {
                _cachedPort = 0;
                _endPoint = null;
                _unavailableReason = "构造 UDP 目标失败 " + e.Message;
                return false;
            }
        }

        private static string EnvOrNull(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch { return null; }
        }

        // =====================================================================
        // 解析（手写，零依赖：热更侧不用 System.Text.Json）
        // =====================================================================

        /// <summary>把 state.json 文本投影成 <see cref="UpdateCheckState"/>；无法解析时返回 null。</summary>
        internal static UpdateCheckState Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            string status;
            if (!TryReadString(json, "status", out status) || string.IsNullOrEmpty(status)) return null;

            var state = new UpdateCheckState
            {
                Status = status,
                CurrentVersion = ReadString(json, "currentVersion"),
                LatestVersion = ReadString(json, "latestVersion"),
                ReleaseTag = ReadString(json, "releaseTag"),
                ReleaseDate = ReadString(json, "releaseDate"),
                ReleaseNotes = ReadString(json, "releaseNotes"),
                ReleaseUrl = ReadString(json, "releaseUrl"),
                DownloadUrl = ReadString(json, "downloadUrl"),
                DownloadSha256 = ReadString(json, "downloadSha256"),
                DownloadSize = ReadLong(json, "downloadSize"),
                Source = ReadString(json, "source"),
                Error = ReadString(json, "error"),
                CheckedAtUnix = ReadLong(json, "checkedAtUnix")
            };
            return state;
        }

        private static string ReadString(string json, string key)
        {
            string value;
            return TryReadString(json, key, out value) ? value : "";
        }

        private static long ReadLong(string json, string key)
        {
            string raw;
            if (!TryReadRaw(json, key, out raw)) return 0;
            long value;
            return long.TryParse(raw.Trim(), out value) ? value : 0;
        }

        private static bool TryReadString(string json, string key, out string value)
        {
            value = "";
            string raw;
            if (!TryReadRaw(json, key, out raw)) return false;
            if (raw.Length < 2 || raw[0] != '"') return false;
            value = Unescape(raw);
            return true;
        }

        /// <summary>
        /// 取一个**扁平**对象里某个键的原始值文本（字符串值含引号）。state.json 只有一层键，
        /// 所以不需要完整 JSON 解析器；但扫描必须跳过字符串内容 —— 否则 releaseNotes 里
        /// 出现 <c>"status"</c> 这样的字面量时会被当成键读走。
        /// </summary>
        private static bool TryReadRaw(string json, string key, out string raw)
        {
            raw = null;
            int depth = 0;
            int i = 0;

            while (i < json.Length)
            {
                char c = json[i];

                if (c == '"')
                {
                    int start = i;
                    i++;
                    while (i < json.Length)
                    {
                        if (json[i] == '\\') { i += 2; continue; }
                        if (json[i] == '"') { i++; break; }
                        i++;
                    }

                    if (i > json.Length) i = json.Length;
                    if (depth != 1) continue;

                    int j = i;
                    while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                    if (j >= json.Length || json[j] != ':') continue;
                    if (!TokenEquals(json, start, i, key)) continue;

                    j++;
                    while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                    if (j >= json.Length) return false;

                    raw = ReadValueToken(json, j);
                    return raw != null;
                }

                if (c == '{' || c == '[') { depth++; i++; continue; }
                if (c == '}' || c == ']') { depth--; i++; continue; }
                i++;
            }

            return false;
        }

        /// <summary>比较 <paramref name="json"/> 里 [start,end) 的字符串 token 内容是否等于 key（key 不含引号与转义）。</summary>
        private static bool TokenEquals(string json, int start, int end, string key)
        {
            // token 形如 "key"；end 指向闭合引号之后
            int contentStart = start + 1;
            int contentLength = end - start - 2;
            if (contentLength != key.Length) return false;
            if (contentLength <= 0) return key.Length == 0;
            return string.CompareOrdinal(json, contentStart, key, 0, contentLength) == 0;
        }

        /// <summary>读一个值的原始文本：字符串含引号，对象/数组返回 null（调用方只要标量）。</summary>
        private static string ReadValueToken(string json, int start)
        {
            char first = json[start];
            if (first == '{' || first == '[') return null;

            if (first == '"')
            {
                int i = start + 1;
                while (i < json.Length)
                {
                    if (json[i] == '\\') { i += 2; continue; }
                    if (json[i] == '"') { i++; break; }
                    i++;
                }
                if (i > json.Length) i = json.Length;
                return json.Substring(start, i - start);
            }

            int cursor = start;
            while (cursor < json.Length && json[cursor] != ',' && json[cursor] != '}' &&
                   json[cursor] != ']' && !char.IsWhiteSpace(json[cursor]))
            {
                cursor++;
            }
            return json.Substring(start, cursor - start);
        }

        /// <summary>去掉两端引号并还原转义（native 的 nlohmann dump 只转义控制符、引号与反斜杠）。</summary>
        private static string Unescape(string quoted)
        {
            if (quoted.Length < 2 || quoted[0] != '"') return quoted;
            int end = quoted.Length;
            if (quoted[end - 1] == '"') end--;

            var builder = new StringBuilder(end);
            for (int i = 1; i < end; i++)
            {
                char c = quoted[i];
                if (c != '\\' || i + 1 >= end)
                {
                    builder.Append(c);
                    continue;
                }

                char next = quoted[++i];
                switch (next)
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'u':
                        {
                            if (i + 4 < end)
                            {
                                int code;
                                if (int.TryParse(quoted.Substring(i + 1, 4),
                                                 System.Globalization.NumberStyles.HexNumber,
                                                 System.Globalization.CultureInfo.InvariantCulture,
                                                 out code))
                                {
                                    builder.Append((char)code);
                                    i += 4;
                                    break;
                                }
                            }
                            builder.Append('u');
                            break;
                        }
                    default:
                        builder.Append(next);
                        break;
                }
            }
            return builder.ToString();
        }
    }
}
