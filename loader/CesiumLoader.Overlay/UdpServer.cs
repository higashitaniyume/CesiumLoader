using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CesiumLoader.Overlay
{
    public sealed class UdpServer : IDisposable
    {
        private UdpClient _client;
        private Thread _thread;
        private CancellationTokenSource _cts;
        private readonly int _port;
        public event Action<PeekMessage> OnPeek;
        public bool IsRunning { get; private set; }

        public UdpServer(int port) { _port = port; }

        public void Start()
        {
            if (_port <= 0) return;
            _cts = new CancellationTokenSource();
            _client = new UdpClient();
            _client.Client.ReceiveTimeout = 500;
            _client.Client.Bind(new IPEndPoint(IPAddress.Loopback, _port));
            IsRunning = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "CesiumOverlay.UDP" };
            _thread.Start();
        }

        private void Loop()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                IPEndPoint remote = null;
                byte[] buf;
                try
                {
                    buf = _client.Receive(ref remote);
                }
                catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                catch { break; }
                if (buf == null || buf.Length == 0) continue;
                if (remote == null) continue;
                // 只接受本机回环
                if (!remote.Address.Equals(IPAddress.Loopback) && !remote.Address.Equals(IPAddress.IPv6Loopback)) continue;
                if (PeekProtocol.TryParse(buf, buf.Length, out var msg))
                {
                    OnPeek?.Invoke(msg);
                }
            }
        }

        public void Stop()
        {
            try { IsRunning = false; _cts?.Cancel(); } catch { }
            try { _client?.Close(); } catch { }
        }

        public void Dispose() { Stop(); }
    }
}
