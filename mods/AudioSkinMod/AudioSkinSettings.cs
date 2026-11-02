using System;
using CesiumLoader.SDK.Configuration;

namespace AudioSkinMod
{
    public sealed class AudioSkinSettings
    {
        public bool Enabled { get; set; } = true;
        public string ActiveScheme { get; set; } = "default";
        public float SfxVolume { get; set; } = 1.0f;
        public float BgmVolume { get; set; } = 1.0f;
        public float VoiceVolume { get; set; } = 1.0f;
        public bool LogEvents { get; set; } = false;

        public static AudioSkinSettings Load(ModConfig config)
        {
            var settings = new AudioSkinSettings();
            if (config == null) return settings;

            if (config.Has("enabled"))
                settings.Enabled = config.GetBool("enabled", settings.Enabled);

            if (config.Has("activeScheme"))
                settings.ActiveScheme = config.GetString("activeScheme", settings.ActiveScheme);

            if (config.Has("sfxVolume"))
                settings.SfxVolume = (float)config.GetDouble("sfxVolume", settings.SfxVolume);

            if (config.Has("bgmVolume"))
                settings.BgmVolume = (float)config.GetDouble("bgmVolume", settings.BgmVolume);

            if (config.Has("voiceVolume"))
                settings.VoiceVolume = (float)config.GetDouble("voiceVolume", settings.VoiceVolume);

            if (config.Has("logEvents"))
                settings.LogEvents = config.GetBool("logEvents", settings.LogEvents);

            return settings;
        }

        public void Save(ModConfig config)
        {
            if (config == null) return;

            config.Set("enabled", Enabled);
            config.Set("activeScheme", ActiveScheme);
            config.Set("sfxVolume", SfxVolume);
            config.Set("bgmVolume", BgmVolume);
            config.Set("voiceVolume", VoiceVolume);
            config.Set("logEvents", LogEvents);
            config.SaveIfDirty();
        }
    }
}
