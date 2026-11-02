using System;
using UnityEngine;
using CesiumLoader.SDK.Configuration;

namespace AudioSampleMod
{
    /// <summary>
    /// AudioSampleMod 配置实体与读取工具。
    /// </summary>
    public sealed class AudioSampleSettings
    {
        public bool Enabled { get; set; } = true;
        public KeyCode PlayKey { get; set; } = KeyCode.F8;
        public float Volume { get; set; } = 0.8f;
        public bool ReplaceButtonClick { get; set; } = true;
        public bool LogSoundEvents { get; set; } = true;

        public static AudioSampleSettings Read(ModConfig config, Action<string> onWarn = null)
        {
            var s = new AudioSampleSettings();
            if (config == null) return s;

            s.Enabled = config.GetBool("enabled", true);

            string keyStr = config.GetString("playKey", "F8");
            if (Enum.TryParse(keyStr, true, out KeyCode key))
            {
                s.PlayKey = key;
            }
            else
            {
                onWarn?.Invoke("无法解析按键 '" + keyStr + "'，回退到 F8。");
                s.PlayKey = KeyCode.F8;
            }

            s.Volume = Mathf.Clamp01(config.GetFloat("volume", 0.8f));
            s.ReplaceButtonClick = config.GetBool("replaceButtonClick", true);
            s.LogSoundEvents = config.GetBool("logSoundEvents", true);

            return s;
        }

        public static void EnsureDefaults(ModConfig config)
        {
            if (config == null) return;
            bool added = false;
            Add(config, "enabled", true, ref added);
            Add(config, "playKey", "F8", ref added);
            Add(config, "volume", 0.8f, ref added);
            Add(config, "replaceButtonClick", true, ref added);
            Add(config, "logSoundEvents", true, ref added);
            if (added) config.SaveIfDirty();
        }

        private static void Add(ModConfig config, string key, object value, ref bool added)
        {
            if (config.Has(key)) return;
            config.Set(key, value);
            added = true;
        }
    }
}
