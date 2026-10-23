using System;
using System.Globalization;
using System.IO;
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
    }
}
