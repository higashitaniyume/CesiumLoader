using System;
using System.IO;
using UnityEngine;
using AudioSampleMod;
using CesiumLoader.SDK.Configuration;
using Xunit;

namespace AudioSampleMod.Tests
{
    public class AudioSampleModTests
    {
        private static ModConfig CreateTestConfig(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioSampleModTests", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            ModConfig.ClearCache();
            return ModConfig.ForModId(name, dir);
        }

        [Fact]
        public void Settings_DefaultValues_AreCorrect()
        {
            var s = new AudioSampleSettings();
            Assert.True(s.Enabled);
            Assert.Equal(KeyCode.F8, s.PlayKey);
            Assert.Equal(0.8f, s.Volume);
            Assert.True(s.ReplaceButtonClick);
            Assert.True(s.LogSoundEvents);
        }

        [Fact]
        public void Settings_ReadFromModConfig_ParsesProperly()
        {
            var config = CreateTestConfig("test_audio_sample_read");
            config.Set("enabled", false);
            config.Set("playKey", "F9");
            config.Set("volume", 0.5f);
            config.Set("replaceButtonClick", false);
            config.Set("logSoundEvents", false);

            var s = AudioSampleSettings.Read(config);
            Assert.False(s.Enabled);
            Assert.Equal(KeyCode.F9, s.PlayKey);
            Assert.Equal(0.5f, s.Volume);
            Assert.False(s.ReplaceButtonClick);
            Assert.False(s.LogSoundEvents);
        }

        [Fact]
        public void Settings_InvalidKey_FallsBackToF8()
        {
            var config = CreateTestConfig("test_audio_sample_bad_key");
            config.Set("playKey", "InvalidKeyName123");

            bool warned = false;
            var s = AudioSampleSettings.Read(config, _ => warned = true);

            Assert.True(warned);
            Assert.Equal(KeyCode.F8, s.PlayKey);
        }

        [Fact]
        public void ConfigDocs_Apply_SetsMultiLanguageDocs()
        {
            var config = CreateTestConfig("test_audio_sample_docs");
            bool changed = AudioSampleConfigDocs.Apply(config);
            Assert.True(changed);
            Assert.True(config.Save());

            string text = File.ReadAllText(config.Path);
            Assert.Contains("F8", text);
            Assert.Contains("playKey", text);
        }
    }
}
