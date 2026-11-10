using System.IO;
using System.Linq;
using System.Text.Json;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.Shared.Speed;
using Xunit;

namespace TurnClockMod.Tests
{
    public sealed class ConfigAndCompatibilityTests
    {
        private static string Resource(string name)
        {
            using var stream = typeof(ConfigAndCompatibilityTests).Assembly.GetManifestResourceStream(name);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        [Fact]
        public void PackagedConfigurationMatchesRuntimeDocumentationInAllLanguages()
        {
            string text = Resource("TurnClock.DefaultConfig");
            Assert.Contains("//", text);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var config = new ModConfig();
            TurnClockConfigDocs.Apply(config);
            string[] languages = { "zh-Hans", "zh-Hant", "en", "ja" };
            var comments = document.RootElement.GetProperty("_comments");
            Assert.Equal(config.Descriptions.Count + 1, document.RootElement.EnumerateObject().Count());
            Assert.Equal(config.Descriptions.Count, comments.EnumerateObject().Count());
            foreach (var field in document.RootElement.EnumerateObject())
            {
                if (field.Name == "_comments") continue;
                Assert.True(config.Descriptions.TryGetValue(field.Name, out var doc), field.Name);
                for (int i = 0; i < languages.Length; i++)
                    Assert.Equal(doc.Descriptions[i], comments.GetProperty(field.Name).GetProperty(languages[i]).GetString());
            }
            Assert.Equal(.2, document.RootElement.GetProperty("speedRefreshSeconds").GetDouble());
            foreach (var source in ClockSourceTable.Default)
                Assert.Equal(source.DefaultOn, document.RootElement.GetProperty(source.Key).GetBoolean());
        }

        [Theory]
        [InlineData("speed=4.000\nhooks=4", 4.0)]
        [InlineData("speed=1.000\nhooks=4", 1.0)]
        public void SharedReaderParsesTheLoadersAcknowledgement(string text, double expected)
        {
            Assert.Equal(expected, NativeSpeedState.Parse(text));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("hooks=4")]
        [InlineData("speed=1\nhooks=0")]
        [InlineData("speed=NaN\nhooks=4")]
        [InlineData("speed=1\nspeed=4\nhooks=4")]
        public void MissingOrInvalidStateIsNotAOneSpeedAcknowledgement(string text)
        {
            Assert.Null(NativeSpeedState.Parse(text));
        }

        [Fact]
        public void SharedHotUpdateIoAvoidsUnsupportedBclApis()
        {
            string code = Resource("TurnClock.NativeStateSource");
            foreach (string forbidden in new[] { "FileStream", "StreamReader", "StreamWriter", "FileInfo", "DirectoryInfo", ".Flush(",
                "DllImport", "File.ReadAllLines", "AppDomain.CurrentDomain.BaseDirectory", "Assembly.Location" })
                Assert.DoesNotContain(forbidden, code);
            Assert.DoesNotContain("File.ReadAllText", code);
            Assert.Contains("SpeedHack.TryGetSpeed", code);
        }
    }
}
