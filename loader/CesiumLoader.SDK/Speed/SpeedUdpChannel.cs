using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CesiumLoader.SDK.Speed
{
    // Only a short managed lock protects the socket/cache; no blocking receive.
    internal sealed class SpeedUdpChannel
    {
        internal const double PollSeconds = .05, FreshSeconds = .5, RetrySeconds = 2;
        internal sealed class Snapshot
        {
            internal long Id, Revision;
            internal double Speed, Base;
            internal bool Ok;
        }
        private Socket _socket;
        private readonly object _gate = new object();
        private readonly bool _background;
        private int _workerGeneration;
        private double _lastAccess;
        private int _port;
        private long _sequence = DateTime.UtcNow.Ticks, _acceptedId, _revision = -1;
        private readonly byte[] _buffer = new byte[1024];
        private readonly Dictionary<long, double> _sent = new Dictionary<long, double>();
        private Snapshot _state;
        private double _sampleAt, _nextSend, _lastNow;
        private string _pending;
        private double _pendingAt;
        internal string Error { get; private set; } = "等待 UDP 状态回执";
        internal Func<double> Clock = () => (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;

        internal SpeedUdpChannel(bool background = false) { _background = background; }

        internal void Reset()
        {
            lock (_gate) ResetLocked();
        }
        private void ResetLocked()
        {
            ++_workerGeneration;
            try { _socket?.Close(); } catch { }
            _socket = null; _port = 0; _state = null; _pending = null;
            _sent.Clear(); _acceptedId = 0; _revision = -1; _nextSend = _lastNow = 0;
            Error = "等待 UDP 状态回执";
        }

        private bool Open(int port)
        {
            if (port != _port) Reset();
            if (port <= 0 || port > 65535) { Error = "未提供有效 CESIUM_SPEED_PORT；需要支持 UDP 的加载器"; return false; }
            try
            {
                if (_socket == null)
                {
                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    _socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
                    _socket.Blocking = false;
                    _port = port;
                    if (_background)
                    {
                        int generation = _workerGeneration;
                        var worker = new Thread(() => ReceiveLoop(generation));
                        worker.IsBackground = true;
                        worker.Start();
                    }
                }
                return true;
            }
            catch (Exception e) { Reset(); Error = "UDP 初始化失败: " + e.GetType().Name; return false; }
        }

        internal Snapshot Read(int port)
        {
            lock (_gate)
            {
                _lastAccess = Clock();
                return ReadLocked(port);
            }
        }

        // Sparse consumers (including 1s startup retries) still need a timely
        // receive, otherwise every reply would be stale before their next call.
        // Stop polling after 1s without a consumer; pending mutations still retry.
        private void ReceiveLoop(int generation)
        {
            for (;;)
            {
                Thread.Sleep(20);
                lock (_gate)
                {
                    if (generation != _workerGeneration) return;
                    double now = Clock();
                    if (_pending != null || (now >= _lastAccess && now - _lastAccess <= 1)) ReadLocked(_port);
                }
            }
        }

        private Snapshot ReadLocked(int port)
        {
            if (!Open(port)) return null;
            double now = Clock();
            if (now < _lastNow) { Reset(); Open(port); }
            _lastNow = now;
            try
            {
                // Bounded work even if a broken peer floods the endpoint. Connected
                // UDP filters other sender addresses/ports in the OS.
                for (int i = 0; i < 16; i++)
                {
                    if (_socket.Available <= 0) break;
                    int length;
                    try { length = _socket.Receive(_buffer); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { break; }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize) { continue; }
                    var reply = Parse(Encoding.ASCII.GetString(_buffer, 0, length));
                    double sentAt;
                    if (reply == null || reply.Id <= _acceptedId || reply.Revision < _revision || !_sent.TryGetValue(reply.Id, out sentAt)) continue;
                    _sent.Remove(reply.Id);
                    if (now - sentAt > FreshSeconds || now < sentAt) continue;
                    // While a mutation is pending, GET replies (including old 1x)
                    // cannot acknowledge that mutation.
                    // Setting clears _sent and no GET is sent while pending, so
                    // every remaining entry is an attempt of this mutation.
                    _acceptedId = reply.Id; _revision = reply.Revision;
                    _state = reply.Ok ? reply : null; _sampleAt = sentAt;
                    if (_pending != null) { _pending = null; _nextSend = 0; }
                    Error = reply.Ok ? null : "加载器拒绝倍率请求";
                }
                if (_pending != null && now - _pendingAt > RetrySeconds)
                {
                    _pending = null; _state = null; _sent.Clear(); Error = "UDP 倍率请求确认超时";
                }
                if (now >= _nextSend)
                {
                    if (_pending != null) SendMutationAttempt(_pending, now);
                    else { long id = ++_sequence; Send(id, "get " + id.ToString(CultureInfo.InvariantCulture), now); }
                }
                if (_state == null || now - _sampleAt > FreshSeconds || now < _sampleAt)
                {
                    _state = null;
                    if (Error == null) Error = "UDP 状态回执已过期";
                    return null;
                }
                return _state;
            }
            catch (Exception e)
            {
                _state = null; Error = "UDP 通信失败: " + e.GetType().Name;
                _nextSend = now + PollSeconds;
                return null;
            }
        }

        internal bool Set(int port, double speed)
        {
            lock (_gate) { _lastAccess = Clock(); return SetLocked(port, speed); }
        }
        private bool SetLocked(int port, double speed)
        {
            if (!Open(port)) return false;
            double now = Clock();
            long id = ++_sequence;
            string request = "set " + id.ToString(CultureInfo.InvariantCulture) + " " + speed.ToString("0.000", CultureInfo.InvariantCulture);
            // Invalidate before send, even if send fails: never reuse stale 1x.
            _state = null; _sent.Clear(); _pending = null; _acceptedId = id - 1;
            try
            {
                SendMutationAttempt(request, now);
                _pending = request; _pendingAt = now;
                Error = "等待 UDP 倍率确认";
                return true;
            }
            catch (Exception e) { Error = "UDP 发送失败: " + e.GetType().Name; return false; }
        }

        private void SendMutationAttempt(string request, double now)
        {
            long attempt = ++_sequence;
            Send(attempt, request + " " + attempt.ToString(CultureInfo.InvariantCulture), now);
        }

        private void Send(long id, string text, double now)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            _socket.Send(bytes);
            // Each transmission has its own timestamp/id. A delayed response to an
            // earlier retry cannot borrow freshness from a later transmission.
            if (_sent.Count >= 32) _sent.Clear();
            _sent[id] = now;
            _nextSend = now + PollSeconds;
        }

        internal static Snapshot Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 1024) return null;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in text.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) return null;
                string key = line.Substring(0, equals), value = line.Substring(equals + 1).Trim();
                if (fields.ContainsKey(key)) return null;
                fields.Add(key, value);
            }
            string protocol, id, revision, ok, speed, basis, hooks, active;
            if (!fields.TryGetValue("protocol", out protocol) || protocol != "1" ||
                !fields.TryGetValue("id", out id) || !fields.TryGetValue("revision", out revision) ||
                !fields.TryGetValue("ok", out ok) || (ok != "0" && ok != "1") ||
                !fields.TryGetValue("speed", out speed) || !fields.TryGetValue("base", out basis) ||
                !fields.TryGetValue("hooks", out hooks) || !fields.TryGetValue("active", out active) || active != "1") return null;
            long requestId, generation, hookCount;
            double current, initial;
            if (!Integer(id, out requestId) || requestId <= 0 || !Integer(revision, out generation) ||
                !Integer(hooks, out hookCount) || hookCount <= 0 || hookCount > 4 ||
                !Decimal(speed, out current) || !Decimal(basis, out initial)) return null;
            return new Snapshot { Id = requestId, Revision = generation, Ok = ok == "1", Speed = current, Base = initial };
        }
        private static bool Integer(string text, out long value)
        {
            value = 0;
            if (text.Length == 0 || text.Length > 19) return false;
            foreach (char c in text)
            {
                if (c < '0' || c > '9' || value > (long.MaxValue - (c - '0')) / 10) return false;
                value = value * 10 + c - '0';
            }
            return true;
        }
        private static bool Decimal(string text, out double value)
        {
            value = 0; bool dot = false; double fraction = 1;
            if (text.Length == 0 || text.Length > 16 || text[0] < '0' || text[0] > '9' || text[text.Length - 1] == '.') return false;
            foreach (char c in text)
            {
                if (c == '.' && !dot) { dot = true; continue; }
                if (c < '0' || c > '9') return false;
                if (dot) { fraction *= .1; value += (c - '0') * fraction; }
                else value = value * 10 + c - '0';
                if (value > 100) return false;
            }
            return value >= 1 && value <= 100;
        }
    }
}
