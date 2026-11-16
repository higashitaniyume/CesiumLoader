using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CesiumLoader.SDK.Updates;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 更新检查的只读客户端（UpdateCheck）。
    ///
    /// 覆盖两件事：
    ///   1) state.json 的解析（加载器写的是扁平淡 JSON，字段多、notes 里可能有换行）；
    ///   2) 发往加载器的本地 UDP 报文（"check" / "open &lt;url&gt;"），与加载器 update/update.cpp 的解析口径一致。
    /// </summary>
    public class UpdateCheckTests : IDisposable
    {
        private readonly UdpClient _listener;
        private readonly int _port;

        public UpdateCheckTests()
        {
            _listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Client.ReceiveTimeout = 3000;
            _port = ((IPEndPoint)_listener.Client.LocalEndPoint).Port;
            UpdateCheck.PortOverride = _port;
            UpdateCheck.ResetCache();
        }

        public void Dispose()
        {
            UpdateCheck.PortOverride = 0;
            UpdateCheck.ResetCache();
            try { _listener.Close(); } catch { }
        }

        private string ReceiveText()
        {
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            var bytes = _listener.Receive(ref endpoint);
            return Encoding.UTF8.GetString(bytes);
        }

        // =====================================================================
        // 解析
        // =====================================================================

        private const string SampleState = @"{
  ""checkedAt"": ""2026-11-16T03:00:00Z"",
  ""checkedAtUnix"": 1763262000,
  ""currentVersion"": ""3.8.4"",
  ""downloadSha256"": ""abc123"",
  ""downloadSize"": 4567,
  ""downloadUrl"": ""https://github.com/higashitaniyume/CesiumLoader/releases/download/modloader-3.8.5/cesium-loader-3.8.5.zip"",
  ""error"": """",
  ""latestVersion"": ""3.8.5"",
  ""releaseDate"": ""2026-11-20T00:00:00Z"",
  ""releaseNotes"": ""第一行\n第二行"",
  ""releaseTag"": ""modloader-3.8.5"",
  ""releaseUrl"": ""https://github.com/higashitaniyume/CesiumLoader/releases/tag/modloader-3.8.5"",
  ""schema"": 1,
  ""source"": ""direct"",
  ""status"": ""update-available""
}";

        [Fact]
        public void Parse_ReadsEveryField()
        {
            var state = UpdateCheck.Parse(SampleState);

            Assert.NotNull(state);
            Assert.Equal("update-available", state.Status);
            Assert.Equal("3.8.4", state.CurrentVersion);
            Assert.Equal("3.8.5", state.LatestVersion);
            Assert.Equal("modloader-3.8.5", state.ReleaseTag);
            Assert.Equal("2026-11-20T00:00:00Z", state.ReleaseDate);
            Assert.Equal("modloader-3.8.5", state.ReleaseTag);
            Assert.Equal("direct", state.Source);
            Assert.Equal("", state.Error);
            Assert.Equal(4567, state.DownloadSize);
            Assert.Equal(1763262000, state.CheckedAtUnix);
            Assert.Contains("modloader-3.8.5", state.ReleaseUrl);
            Assert.Contains("cesium-loader-3.8.5.zip", state.DownloadUrl);
            Assert.True(state.HasUpdate);
            Assert.False(state.IsUpToDate);
            Assert.False(state.IsError);
        }

        [Fact]
        public void Parse_UnescapesReleaseNotes()
        {
            var state = UpdateCheck.Parse(SampleState);

            Assert.Equal("第一行\n第二行", state.ReleaseNotes);
        }

        [Fact]
        public void Parse_DoesNotTreatJsonLookingTextInStringsAsKeys()
        {
            // 更新说明里出现了 ""status"": ... 这种字面量时，不能被当成真正的 status 键。
            const string json = @"{
  ""releaseNotes"": ""fix: \""status\"": \""up-to-date\"" noise"",
  ""status"": ""update-available"",
  ""latestVersion"": ""3.8.5""
}";

            var state = UpdateCheck.Parse(json);

            Assert.NotNull(state);
            Assert.Equal("update-available", state.Status);
            Assert.Equal("3.8.5", state.LatestVersion);
            Assert.Contains("up-to-date", state.ReleaseNotes);
        }

        [Fact]
        public void Parse_RequiresStatus()
        {
            Assert.Null(UpdateCheck.Parse(""));
            Assert.Null(UpdateCheck.Parse("not json"));
            Assert.Null(UpdateCheck.Parse(@"{ ""latestVersion"": ""3.8.5"" }"));
        }

        [Fact]
        public void HasUpdate_RequiresUpdateAvailableAndVersion()
        {
            var state = UpdateCheck.Parse(SampleState);
            state.Status = "up-to-date";
            Assert.False(state.HasUpdate);

            state = UpdateCheck.Parse(SampleState);
            state.LatestVersion = "";
            Assert.False(state.HasUpdate);
        }

        [Theory]
        [InlineData("update-available", true, false, false)]
        [InlineData("up-to-date", false, true, false)]
        [InlineData("error", false, false, true)]
        [InlineData("disabled", false, false, false)]
        public void Flags_FollowStatus(string status, bool hasUpdate, bool upToDate, bool error)
        {
            var state = UpdateCheck.Parse(SampleState);
            state.Status = status;

            Assert.Equal(hasUpdate, state.HasUpdate);
            Assert.Equal(upToDate, state.IsUpToDate);
            Assert.Equal(error, state.IsError);
        }

        // =====================================================================
        // UDP 报文
        // =====================================================================

        [Fact]
        public void RequestCheck_SendsCheckDatagram()
        {
            Assert.True(UpdateCheck.IsAvailable);
            Assert.True(UpdateCheck.RequestCheck());

            Assert.Equal("check", ReceiveText());
        }

        [Fact]
        public void OpenUrl_SendsOpenDatagram()
        {
            const string url = "https://github.com/higashitaniyume/CesiumLoader/releases/tag/modloader-3.8.5";

            Assert.True(UpdateCheck.OpenUrl(url));
            Assert.Equal("open " + url, ReceiveText());
        }

        [Theory]
        [InlineData("http://github.com/x")]
        [InlineData("https://github.com/has space")]
        [InlineData("https://github.com/line\nbreak")]
        [InlineData("")]
        public void OpenUrl_RejectsUnsupportedInput(string url)
        {
            Assert.False(UpdateCheck.OpenUrl(url));

            // 没有报文发出去
            Assert.Equal(0, _listener.Available);
        }

        [Fact]
        public void OpenReleasePage_FallsBackWhenNoState()
        {
            // 没有状态文件时用兜底发布页（这里只断言报文，不依赖磁盘）。
            Assert.True(UpdateCheck.OpenReleasePage());
            var message = ReceiveText();
            Assert.StartsWith("open https://github.com/", message);
        }
    }
}
