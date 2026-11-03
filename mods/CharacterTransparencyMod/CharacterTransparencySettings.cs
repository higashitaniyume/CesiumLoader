using System;
using CesiumLoader.SDK.Configuration;
using UnityEngine;

namespace CharacterTransparencyMod
{
    /// <summary>从 mods\CharacterTransparencyMod\config.json 读取的配置。</summary>
    internal sealed class CharacterTransparencySettings
    {
        internal const string DefaultKeyName = "H";
        internal const bool DefaultHoldMode = true;
        internal const float DefaultOpacity = 0.36f;
        internal const float DefaultHoldSeconds = 0.0f;

        public bool Enabled = true;
        public KeyCode Key = KeyCode.H;
        public bool HoldMode = DefaultHoldMode;
        public float Opacity = DefaultOpacity;
        public float HoldSeconds = DefaultHoldSeconds;

        public static CharacterTransparencySettings Read(ModConfig config, Action<string> warn)
        {
            var value = new CharacterTransparencySettings();
            if (config == null) return value;

            value.Enabled = config.GetBool("enabled", true);
            value.Key = ParseKey(config.GetString("toggleKey", DefaultKeyName), warn);
            value.HoldMode = config.GetBool("holdMode", DefaultHoldMode);
            value.Opacity = ClampOpacity(config.GetFloat("opacity", DefaultOpacity));
            value.HoldSeconds = Math.Max(0f, config.GetFloat("holdSeconds", DefaultHoldSeconds));
            return value;
        }

        /// <summary>补齐缺失的默认配置键，不覆盖已有的配置项。</summary>
        public static void EnsureDefaults(ModConfig config)
        {
            if (config == null) return;

            bool added = CharacterTransparencyConfigDocs.Apply(config);
            Add(config, "enabled", true, ref added);
            Add(config, "toggleKey", DefaultKeyName, ref added);
            Add(config, "holdMode", DefaultHoldMode, ref added);
            Add(config, "opacity", DefaultOpacity, ref added);
            Add(config, "holdSeconds", DefaultHoldSeconds, ref added);
            if (added) config.SaveIfDirty();
        }

        private static void Add(ModConfig config, string key, object value, ref bool added)
        {
            if (config.Has(key)) return;
            config.Set(key, value);
            added = true;
        }

        internal static float ClampOpacity(float val)
        {
            if (val > 1.0f) val /= 100f;
            if (val < 0f) return 0f;
            if (val > 1f) return 1f;
            return val;
        }

        internal static KeyCode ParseKey(string name, Action<string> warn)
        {
            if (string.IsNullOrEmpty(name)) return KeyCode.H;
            string text = name.Trim();

            if (string.Equals(text, "H", StringComparison.OrdinalIgnoreCase)) return KeyCode.H;
            if (text == "`" || text == "~" || text == "·") return KeyCode.BackQuote;
            if (text == "Tab") return KeyCode.Tab;
            if (text == "Escape" || text == "Esc") return KeyCode.Escape;
            if (text == "Space" || text == "空格") return KeyCode.Space;

            KeyCode parsed;
            if (Enum.TryParse(text, true, out parsed) && parsed != KeyCode.None) return parsed;

            if (warn != null) warn("toggleKey 无法识别: " + name + "，改用 H");
            return KeyCode.H;
        }
    }
}
