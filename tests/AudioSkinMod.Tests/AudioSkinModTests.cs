using System;
using System.IO;
using AudioSkinMod;
using CesiumLoader.SDK.Audio;
using CesiumLoader.SDK.Configuration;
using Xunit;

namespace AudioSkinMod.Tests
{
    public class AudioSkinModTests
    {
        [Fact]
        public void Settings_DefaultValues_AreCorrect()
        {
            var settings = new AudioSkinSettings();
            Assert.True(settings.Enabled);
            Assert.Equal("default", settings.ActiveScheme);
            Assert.Equal(1.0f, settings.SfxVolume);
            Assert.Equal(1.0f, settings.BgmVolume);
            Assert.Equal(1.0f, settings.VoiceVolume);
            Assert.False(settings.LogEvents);
        }

        [Fact]
        public void Settings_LoadFromConfig_PopulatesProperties()
        {
            var config = ModConfig.ForModId("TestAudioSkinConfig");
            config.Set("enabled", false);
            config.Set("activeScheme", "custom_pack");
            config.Set("sfxVolume", 0.7);
            config.Set("bgmVolume", 0.5);
            config.Set("voiceVolume", 0.8);
            config.Set("logEvents", true);

            var settings = AudioSkinSettings.Load(config);

            Assert.False(settings.Enabled);
            Assert.Equal("custom_pack", settings.ActiveScheme);
            Assert.Equal(0.7f, settings.SfxVolume, 2);
            Assert.Equal(0.5f, settings.BgmVolume, 2);
            Assert.Equal(0.8f, settings.VoiceVolume, 2);
            Assert.True(settings.LogEvents);
        }

        [Fact]
        public void SchemeManifest_Deserialization_WorksWithCesiumJson()
        {
            string json = @"
{
  ""id"": ""arcade"",
  ""name"": ""Arcade Pack"",
  ""author"": ""Tester"",
  ""description"": ""8-bit arcade retro sounds"",
  ""rules"": [
    {
      ""configId"": 1001,
      ""eventName"": ""UI_Common_Click01"",
      ""action"": ""replace"",
      ""file"": ""click.wav"",
      ""volume"": 0.85
    },
    {
      ""configId"": 1002,
      ""action"": ""mute""
    },
    {
      ""configId"": 1003,
      ""action"": ""redirect"",
      ""redirectId"": 2001
    }
  ]
}";

            var manifest = CesiumJson.Deserialize<AudioSchemeManifest>(json);
            Assert.NotNull(manifest);
            Assert.Equal("arcade", manifest.Id);
            Assert.Equal("Arcade Pack", manifest.Name);
            Assert.Equal(3, manifest.Rules.Count);

            Assert.Equal(1001, manifest.Rules[0].ConfigId);
            Assert.Equal("UI_Common_Click01", manifest.Rules[0].EventName);
            Assert.Equal("replace", manifest.Rules[0].Action);
            Assert.Equal("click.wav", manifest.Rules[0].File);
            Assert.Equal(0.85f, manifest.Rules[0].Volume, 2);

            Assert.Equal("mute", manifest.Rules[1].Action);
            Assert.Equal("redirect", manifest.Rules[2].Action);
            Assert.Equal(2001, manifest.Rules[2].RedirectId);
        }

        [Fact]
        public void ConfigDocs_Apply_DoesNotThrow()
        {
            var config = ModConfig.ForModId("TestAudioSkinDocs");
            var ex = Record.Exception(() => AudioSkinConfigDocs.Apply(config));
            Assert.Null(ex);
        }
    }
}
