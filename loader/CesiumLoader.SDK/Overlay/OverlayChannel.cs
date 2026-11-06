using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CesiumLoader.SDK.Overlay
{
    /// <summary>
    /// Mod -> WPF Overlay 进程的数据通道（127.0.0.1 UDP 单向推送）。
    /// 与 <c>WindowFocus</c> 同款模式：System.Net.Sockets.Socket + CESIUM_OVERLAY_PORT。
    /// 端口由加载器在 127.0.0.1 上动态分配(bind 0)，写入同进程环境变量 CESIUM_OVERLAY_PORT ——
    /// 每进程一个端口，多开时 datagram 只进同进程的 Overlay，不串号。
    /// CESIUM_OVERLAY_PORT 不存在或非法时 <see cref="IsAvailable"/> 为 false，调用方应降级。
    ///
    /// 协议：一条 datagram = 一个 UTF-8 JSON 对象（无长度前缀上限 8192 字节）。
    /// Shop / Relic 面板走 kind="peek"，见 <see cref="SendPeek"/>。
    /// </summary>
    public static class OverlayChannel
    {
        private static readonly object _lock = new object();
        private static Socket _socket;
        private static EndPoint _endPoint;
        private static int _cachedPort;
        private static string _unavailableReason;

        internal static int PortOverride { get; set; }

        internal static void ResetCache()
        {
            lock (_lock)
            {
                if (_socket != null) { try { _socket.Close(); } catch { } _socket = null; }
                _endPoint = null;
                _cachedPort = 0;
                _unavailableReason = null;
            }
        }

        public static bool IsAvailable
        {
            get { lock (_lock) { return IsAvailableLocked(); } }
        }

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
        /// 推送一个 Peek 面板（商店或筹码）快照。可见性翻转与文本变化时调用即可，
        /// 调用方应做去重，不要每帧发（LateUpdate 60fps 会把流量拉爆）。
        /// </summary>
        /// <param name="panel">"shop" 或 "relic"</param>
        /// <param name="visible">false 时 Overlay 侧隐藏该面板</param>
        /// <param name="text">FairyGUI BBCode 文本（Overy 侧负责解析颜色/加粗）</param>
        /// <param name="fontSize">字号 12-36</param>
        /// <param name="panelWidth">面板宽度像素</param>
        /// <param name="offsetX">距游戏窗口右边缘的水平偏移</param>
        /// <param name="offsetY">距游戏窗口顶部的垂直偏移</param>
        /// <returns>datagram 是否已发出（通道不可用/发送失败为 false，不抛异常）</returns>
        public static bool SendPeek(string panel, bool visible, string text, int fontSize, float panelWidth, float offsetX, float offsetY)
        {
            if (string.IsNullOrEmpty(panel)) return false;
            if (panel != "shop" && panel != "relic") return false;
            string body = text ?? string.Empty;
            if (body.Length > 6000) body = body.Substring(0, 6000); // 留出 JSON 与编码开销，单 datagram 上限 8192

            // 手写 JSON：避免引入 System.Text.Json / MiniJson 的字段反射开销，
            // 同时只用 StringBuilder + 一次 UTF8 编码，HybridCLR 可用面更窄。
            var sb = new StringBuilder();
            sb.Append("{\"v\":1,\"kind\":\"peek\",\"panel\":\"").Append(panel).Append("\",");
            sb.Append("\"visible\":").Append(visible ? "true" : "false").Append(",");
            sb.Append("\"text\":\"").Append(EscapeJson(body)).Append("\",");
            sb.Append("\"fontSize\":").Append(fontSize.ToString(CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"panelWidth\":").Append(panelWidth.ToString("0.##", CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"offsetX\":").Append(offsetX.ToString("0.##", CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"offsetY\":").Append(offsetY.ToString("0.##", CultureInfo.InvariantCulture)).Append(",");
            long ts = 0;
            try { ts = DateTime.UtcNow.Ticks; } catch { }
            sb.Append("\"ts\":").Append(ts.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');

            return SendJson(sb.ToString());
        }

        private static bool SendJson(string json)
        {
            lock (_lock)
            {
                if (!IsAvailableLocked()) return false;
                int port = _cachedPort;
                if (port <= 0) { _unavailableReason = "UDP 端口未解析(CESIUM_OVERLAY_PORT)"; return false; }
                byte[] bytes;
                try { bytes = Utf8Bytes(json); }
                catch (Exception e) { _unavailableReason = "编码失败 " + Describe(e); return false; }
                if (bytes.Length > 8192) { _unavailableReason = "datagram 超长 " + bytes.Length; return false; }
                try
                {
                    if (_socket == null)
                        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
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

        private static string EscapeJson(string s)
        {
            // 最小转义：引号/反斜杠/换行只发必需的；中文与 emoji 原样保留。
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string repl = null;
                if (c == '"') repl = "\\\"";
                else if (c == '\\') repl = "\\\\";
                else if (c == '\n') repl = "\\n";
                else if (c == '\r') repl = "\\r";
                else if (c == '\t') repl = "\\t";
                else if (c < 0x20) repl = "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture);
                if (repl != null)
                {
                    if (sb == null) { sb = new StringBuilder(s.Length + 16); sb.Append(s.Substring(0, i)); }
                    sb.Append(repl);
                }
                else if (sb != null) sb.Append(c);
            }
            return sb != null ? sb.ToString() : s;
        }

        /// <summary>手写 UTF-8，与 WindowFocus.Utf8Bytes 同口径（HybridCLR 下 Encoding 不可信）。</summary>
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
                        if (low >= 0xDC00 && low <= 0xDFFF) { code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00); i++; paired = true; }
                    }
                    if (!paired) code = 0xFFFD;
                }
                else if (code >= 0xDC00 && code <= 0xDFFF) code = 0xFFFD;
                if (code < 0x80) bytes.Add((byte)code);
                else if (code < 0x800) { bytes.Add((byte)(0xC0 | (code >> 6))); bytes.Add((byte)(0x80 | (code & 0x3F))); }
                else if (code < 0x10000) { bytes.Add((byte)(0xE0 | (code >> 12))); bytes.Add((byte)(0x80 | ((code >> 6) & 0x3F))); bytes.Add((byte)(0x80 | (code & 0x3F))); }
                else { bytes.Add((byte)(0xF0 | (code >> 18))); bytes.Add((byte)(0x80 | ((code >> 12) & 0x3F))); bytes.Add((byte)(0x80 | ((code >> 6) & 0x3F))); bytes.Add((byte)(0x80 | (code & 0x3F))); }
            }
            return bytes.ToArray();
        }

        private static bool IsAvailableLocked()
        {
            int port = ResolvePortLocked();
            if (port <= 0)
            {
                _unavailableReason = "未找到 CESIUM_OVERLAY_PORT(需要加载器启用 overlayEnabled，且 overlay 子进程就绪)";
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
            string env = EnvOrNull("CESIUM_OVERLAY_PORT");
            if (!string.IsNullOrEmpty(env))
            {
                string trimmed = env.Trim();
                int p;
                if (int.TryParse(trimmed, out p) && p > 0 && p <= 65535) return p;
            }
            return 0;
        }

        private static string EnvOrNull(string name)
        {
            try { var v = Environment.GetEnvironmentVariable(name); return string.IsNullOrEmpty(v) ? null : v; }
            catch { return null; }
        }

        private static string Describe(Exception e)
        {
            try { return e.GetType().Name + ": " + e.Message; } catch { return "未知异常"; }
        }
    }
}
