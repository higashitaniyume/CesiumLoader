using System;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumLoader.SDK.Windowing;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 窗口置前通道 (WindowFocus)。热更程序集无法 P/Invoke, mod 只能写加载器监听的文件,
    /// 这里用临时目录完整覆盖"可用/不可用/写请求/每次令牌不同"。
    /// </summary>
    public class WindowFocusTests : IDisposable
    {
        private readonly string _dir;

        public WindowFocusTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cesium-windowfocus-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            WindowFocus.DirectoryOverride = _dir;
            WindowFocus.ResetCache();
        }

        public void Dispose()
        {
            WindowFocus.DirectoryOverride = null;
            WindowFocus.ResetCache();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string StatePath => Path.Combine(_dir, "state.txt");
        private string RequestPath => Path.Combine(_dir, "request.txt");

        private void WriteState(string text) => File.WriteAllText(StatePath, text);

        [Fact]
        public void Available_RequiresLoaderState()
        {
            Assert.False(WindowFocus.IsAvailable);
            Assert.False(WindowFocus.RequestForeground());
            Assert.False(File.Exists(RequestPath));

            WriteState("version=2.5.5\r\navailable=0\r\n");
            Assert.False(WindowFocus.IsAvailable);
            Assert.False(WindowFocus.RequestForeground());
            Assert.False(File.Exists(RequestPath));

            WriteState("version=2.5.5\r\navailable=1\r\n");
            Assert.True(WindowFocus.IsAvailable);
            Assert.True(WindowFocus.RequestForeground());
            Assert.True(File.Exists(RequestPath));
        }

        [Fact]
        public void RequestForeground_WritesAChangingToken()
        {
            WriteState("version=2.5.5\r\navailable=1\r\n");

            Assert.True(WindowFocus.RequestForeground());
            long first;
            Assert.True(long.TryParse(File.ReadAllText(RequestPath).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out first));

            Assert.True(WindowFocus.RequestForeground());
            long second;
            Assert.True(long.TryParse(File.ReadAllText(RequestPath).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out second));

            // 加载器只处理内容变化: 每次请求的令牌必须不同。
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void Describe_ReportsAvailabilityAndDirectory()
        {
            WriteState("version=2.5.5\r\navailable=1\r\n");
            string text = WindowFocus.Describe();
            Assert.Contains("可用", text);
            Assert.Contains(_dir, text);

            File.Delete(StatePath);
            text = WindowFocus.Describe();
            Assert.Contains("不可用", text);
        }

        [Fact]
        public void Availability_RecoversWhenLoaderWritesStateLater()
        {
            Assert.False(WindowFocus.IsAvailable);
            WriteState("version=2.5.5\r\navailable=1\r\n");
            Assert.True(WindowFocus.IsAvailable);
        }

        // ---------- 提示音 (2.7.1) ----------

        [Fact]
        public void SupportsSound_RequiresAudioLine()
        {
            WriteState("version=2.7.0\r\navailable=1\r\n");
            Assert.False(WindowFocus.SupportsSound);   // 旧加载器没有 audio 行

            WriteState("version=2.7.1\r\navailable=1\r\naudio=0\r\n");
            Assert.False(WindowFocus.SupportsSound);

            WriteState("version=2.7.1\r\navailable=1\r\naudio=wav\r\n");
            Assert.True(WindowFocus.SupportsSound);
        }

        [Fact]
        public void RequestForeground_WithSound_WritesTokenAndPath()
        {
            WriteState("version=2.7.1\r\navailable=1\r\naudio=wav\r\n");
            string sound = Path.Combine(_dir, "sounds", "your-turn.wav");

            Assert.True(WindowFocus.RequestForeground(sound));

            string[] lines = File.ReadAllLines(RequestPath);
            Assert.Equal(2, lines.Length);
            Assert.True(long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
            Assert.Equal(sound, lines[1]);
        }

        [Fact]
        public void RequestForeground_WithoutSound_WritesSingleLine()
        {
            WriteState("version=2.7.1\r\navailable=1\r\naudio=wav\r\n");

            Assert.True(WindowFocus.RequestForeground(null));
            Assert.Single(File.ReadAllLines(RequestPath));

            Assert.True(WindowFocus.RequestForeground("   "));
            Assert.Single(File.ReadAllLines(RequestPath));
        }

        [Fact]
        public void RequestForeground_DropsRelativeOrMultilineSound()
        {
            WriteState("version=2.7.1\r\navailable=1\r\naudio=wav\r\n");

            // 相对路径: 加载器会按自己的当前目录解析, 不能发出去。
            Assert.True(WindowFocus.RequestForeground(@"sounds\your-turn.wav"));
            Assert.Single(File.ReadAllLines(RequestPath));

            // 换行会破坏请求文件的行结构。
            Assert.True(WindowFocus.RequestForeground("C:\\a.wav\r\nC:\\b.wav"));
            Assert.Single(File.ReadAllLines(RequestPath));
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
        public void RequestForeground_UnavailableChannel_DoesNotWriteSoundRequest()
        {
            string sound = Path.Combine(_dir, "ding.wav");
            Assert.False(WindowFocus.RequestForeground(sound));
            Assert.False(File.Exists(RequestPath));
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
    }
}
