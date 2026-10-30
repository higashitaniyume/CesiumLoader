using System;
using System.IO;
using CesiumLoader.SDK.Configuration;
using Xunit;

namespace ChatDurationMod.Tests
{
    public class ChatDurationSettingsTests
    {
        private static ModConfig NewConfig()
        {
            string modId = "ChatDurationModTests-" + Guid.NewGuid().ToString("N");
            string directory = Path.Combine(Path.GetTempPath(), modId);
            return ModConfig.ForModId(modId, directory);
        }

        [Fact]
        public void EnsureDefaults_UsesFifteenSeconds()
        {
            ModConfig config = NewConfig();

            ChatDurationSettings.EnsureDefaults(config);
            ChatDurationSettings settings = ChatDurationSettings.Read(config, null);

            Assert.True(settings.Enabled);
            Assert.Equal(15, settings.DurationSeconds);
        }

        [Fact]
        public void Read_NormalizesOutOfRangeValueAndWarns()
        {
            ModConfig config = NewConfig();
            config.Set("durationSeconds", 999);
            string warning = null;

            ChatDurationSettings settings = ChatDurationSettings.Read(config, message => warning = message);

            Assert.Equal(ChatDurationRules.MaxSeconds, settings.DurationSeconds);
            Assert.NotNull(warning);
            Assert.Contains("999", warning);
        }

        [Fact]
        public void Read_HonorsDisabledSwitch()
        {
            ModConfig config = NewConfig();
            config.Set("enabled", false);

            ChatDurationSettings settings = ChatDurationSettings.Read(config, null);

            Assert.False(settings.Enabled);
            Assert.Equal(15, settings.DurationSeconds);
        }

        [Fact]
        public void Read_WithoutConfigUsesDefaults()
        {
            ChatDurationSettings settings = ChatDurationSettings.Read(null, null);

            Assert.True(settings.Enabled);
            Assert.Equal(ChatDurationRules.DefaultSeconds, settings.DurationSeconds);
        }

        [Fact]
        public void ConfigDocs_CoverEveryLanguageForBothKeys()
        {
            ModConfig config = NewConfig();

            Assert.True(ChatDurationConfigDocs.Apply(config));
            Assert.True(config.Save());

            string json = File.ReadAllText(config.Path);
            Assert.Contains("zh-Hans", json);
            Assert.Contains("zh-Hant", json);
            Assert.Contains("\"en\"", json);
            Assert.Contains("ja", json);
            Assert.Contains("\"enabled\"", json);
            Assert.Contains("\"durationSeconds\"", json);
        }
    }
}
