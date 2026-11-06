using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CesiumLoader.SDK.Windowing;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 窗口置前通道 (WindowFocus)。热更程序集无法 P/Invoke, mod 只能发本地 UDP,
    /// 这里用回环 UDP 监听端完整覆盖"可用/不可用/发报文/每次令牌不同"。
    /// (旧版文件通道测试已替换为 UDP 版, 与加载器的 bind 0 + CESIUM_FOCUS_PORT 发现一致。)
    /// </summary>
    public class WindowFocusTests : IDisposable
    {
        private readonly UdpClient _listener;
        private readonly int _port;

        public WindowFocusTests()
        {
            _listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Client.ReceiveTimeout = 3000;
            _port = ((IPEndPoint)_listener.Client.LocalEndPoint).Port;
            WindowFocus.PortOverride = _port;
            WindowFocus.ResetCache();
        }

        public void Dispose()
        {
            WindowFocus.PortOverride = 0;
            WindowFocus.ResetCache();
            try { _listener.Close(); } catch { }
        }

        private string ReceiveText()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            byte[] bytes = _listener.Receive(ref ep);
            return Encoding.UTF8.GetString(bytes);
        }

        [Fact]
        public void Available_RequiresLoaderPort()
        {
            // 有端口即可用(收到一次请求即证明监听端在工作)
            Assert.True(WindowFocus.IsAvailable);
            Assert.True(WindowFocus.RequestForeground());
            Assert.NotEmpty(ReceiveText());

            WindowFocus.PortOverride = 0;
            WindowFocus.ResetCache();
            Assert.False(WindowFocus.IsAvailable);
            Assert.False(WindowFocus.RequestForeground());
        }

        [Fact]
        public void RequestForeground_SendsAChangingToken()
        {
            Assert.True(WindowFocus.RequestForeground());
            long first;
            Assert.True(long.TryParse(ReceiveText().Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out first));

            Assert.True(WindowFocus.RequestForeground());
            long second;
            Assert.True(long.TryParse(ReceiveText().Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out second));

            // 每次请求的令牌必须不同(加载器靠令牌去重旧文件版的残留请求)。
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void Describe_ReportsAvailabilityAndEndpoint()
        {
            string text = WindowFocus.Describe();
            Assert.Contains("可用", text);
            Assert.Contains(_port.ToString(), text);

            WindowFocus.PortOverride = 0;
            WindowFocus.ResetCache();
            text = WindowFocus.Describe();
            Assert.Contains("不可用", text);
        }

        [Fact]
        public void Availability_RecoversWhenLoaderAppearsLater()
        {
            WindowFocus.PortOverride = 0;
            WindowFocus.ResetCache();
            Assert.False(WindowFocus.IsAvailable);

            WindowFocus.PortOverride = _port;
            WindowFocus.ResetCache();
            Assert.True(WindowFocus.IsAvailable);
        }

        // ---------- 提示音 ----------

        [Fact]
        public void SupportsSound_MirrorsAvailability()
        {
            Assert.True(WindowFocus.SupportsSound);   // UDP 就绪即支持 WAV

            WindowFocus.PortOverride = 0;
            WindowFocus.ResetCache();
            Assert.False(WindowFocus.SupportsSound);
        }

        [Fact]
        public void RequestForeground_WithSound_SendsTokenAndPath()
        {
            string sound = @"C:\sounds\your-turn.wav";

            Assert.True(WindowFocus.RequestForeground(sound));

            string[] lines = ReceiveText().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.True(long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
            Assert.Equal(sound, lines[1]);
        }

        [Fact]
        public void RequestForeground_WithoutSound_SendsSingleLine()
        {
            Assert.True(WindowFocus.RequestForeground(null));
            Assert.Single(ReceiveText().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries));

            Assert.True(WindowFocus.RequestForeground("   "));
            Assert.Single(ReceiveText().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries));
        }

        [Fact]
        public void RequestForeground_DropsRelativeOrMultilineSound()
        {
            // 相对路径: 加载器会按自己的当前目录解析, 不能发出去。
            Assert.True(WindowFocus.RequestForeground(@"sounds\your-turn.wav"));
            Assert.Single(ReceiveText().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries));

            // 换行会破坏报文的行结构。
            Assert.True(WindowFocus.RequestForeground("C:\\a.wav\r\nC:\\b.wav"));
            Assert.Single(ReceiveText().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries));
        }

        [Theory]
        [InlineData("C:\\sounds\\ding.wav", true)]
        [InlineData("C:/sounds/ding.wav", true)]
        [InlineData("\\\\server\\share\\ding.wav", true)]
        [InlineData("C:ding.wav", false)]          // 驱动器相对, 不是绝对路径
        [InlineData("sounds\\ding.wav", false)]
        [InlineData("/sounds/ding.wav", false)]    // 无驱动器, 加载器不接受
        [InlineData("", false)]
        public void IsAbsolutePath_MatchesLoaderRules(string path, bool expected)
        {
            Assert.Equal(expected, WindowFocus.IsAbsolutePath(path));
        }

        [Fact]
        public void RequestForeground_UnavailableChannel_DoesNotSend()
        {
            WindowFocus.PortOverride = 0;
            WindowFocus.ResetCache();

            string sound = @"C:\ding.wav";
            Assert.False(WindowFocus.RequestForeground(sound));
            Assert.Throws<SocketException>(() => ReceiveText());
        }

        [Fact]
        public void Utf8Bytes_MatchesDotNetEncoding()
        {
            // 提示音路径可能是中文: 手写编码必须与 BCL 完全一致(含 4 字节代理对)。
            foreach (string text in new[]
            {
                "C:\\sounds\\ding.wav",
                "C:\\用户\\提示音.wav",
                "C:\\emoji\\\U0001F514.wav",
                "C:\\mixed\\中文-abc-123.wav",
            })
            {
                Assert.Equal(Encoding.UTF8.GetBytes(text), WindowFocus.Utf8Bytes(text));
            }
        }

        [Fact]
        public void BuildRequestText_LaysOutTokenAndPath()
        {
            string text = WindowFocus.BuildRequestText(12345, "C:\\a\\ding.wav");
            Assert.Equal("12345\r\nC:\\a\\ding.wav\r\n", text);
        }

        [Fact]
        public void BuildRequestText_AddsAlwaysLineOnlyWithSound()
        {
            // 默认(仅后台播放): 不写第三行。
            Assert.Equal("1\r\nC:\\a\\ding.wav\r\n",
                WindowFocus.BuildRequestText(1, "C:\\a\\ding.wav", false));

            // 前台也播放: 第三行 always=1。
            Assert.Equal("1\r\nC:\\a\\ding.wav\r\nalways=1\r\n",
                WindowFocus.BuildRequestText(1, "C:\\a\\ding.wav", true));

            // 没有提示音时, always 没有意义, 不写。
            Assert.Equal("1\r\n", WindowFocus.BuildRequestText(1, null, true));
        }

        [Fact]
        public void RequestForeground_WithAlwaysPlay_SendsThirdLine()
        {
            string sound = @"C:\ding.wav";

            Assert.True(WindowFocus.RequestForeground(sound, true));

            string[] lines = ReceiveText().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            Assert.Equal(sound, lines[1]);
            Assert.Equal("always=1", lines[2]);
        }
    }
}
