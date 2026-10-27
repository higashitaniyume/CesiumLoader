using System;
using CesiumLoader.SDK.Configuration;
using UnityEngine;

namespace HideHudMod
{
    /// <summary>从 mods\HideHudMod\config.json 读取的设置。</summary>
    internal sealed class HideHudSettings
    {
        internal const string DefaultKeyName = "BackQuote";
        internal const float DefaultHoldSeconds = 0.35f;
        internal const string DefaultPressMode = "Hold";

        public bool Enabled = true;
        public KeyCode Key = KeyCode.BackQuote;
        public HudPressMode Mode = HudPressMode.Hold;
        public float HoldSeconds = DefaultHoldSeconds;

        /// <summary>强制隐藏全套 HUD；开启时忽略下面四项分类。</summary>
        public bool HideAllHud;

        public bool HideHandCards = true;
        public bool HideMonsterDisplay = true;
        public bool HideAvatars = true;
        public bool HideOther = true;

        /// <summary>屏幕提示（默认关闭，避免隐藏 HUD 时还弹一条提示）。</summary>
        public bool Notify;

        public static HideHudSettings Read(ModConfig config, Action<string> warn)
        {
            var value = new HideHudSettings();
            if (config == null) return value;

            value.Enabled = config.GetBool("enabled", true);
            value.Key = ParseKey(config.GetString("toggleKey", DefaultKeyName), warn);
            value.Mode = ParseMode(config.GetString("pressMode", DefaultPressMode), warn);
            value.HoldSeconds = ClampHoldSeconds(config.GetFloat("holdSeconds", DefaultHoldSeconds));

            value.HideAllHud = config.GetBool("hideAllHud", false);
            value.HideHandCards = config.GetBool("hideHandCards", true);
            value.HideMonsterDisplay = config.GetBool("hideMonsterDisplay", true);
            value.HideAvatars = config.GetBool("hideAvatars", true);
            value.HideOther = config.GetBool("hideOther", true);
            value.Notify = config.GetBool("notify", false);
            return value;
        }

        /// <summary>只补齐缺失键，不覆盖用户已经写好的值。</summary>
        public static void EnsureDefaults(ModConfig config)
        {
            if (config == null) return;

            bool added = false;
            Add(config, "enabled", true, ref added);
            Add(config, "toggleKey", DefaultKeyName, ref added);
            Add(config, "pressMode", DefaultPressMode, ref added);
            Add(config, "holdSeconds", DefaultHoldSeconds, ref added);
            Add(config, "hideAllHud", false, ref added);
            Add(config, "hideHandCards", true, ref added);
            Add(config, "hideMonsterDisplay", true, ref added);
            Add(config, "hideAvatars", true, ref added);
            Add(config, "hideOther", true, ref added);
            Add(config, "notify", false, ref added);
            if (added) config.Save();
        }

        private static void Add(ModConfig config, string key, object value, ref bool added)
        {
            if (config.Has(key)) return;
            config.Set(key, value);
            added = true;
        }

        internal static KeyCode ParseKey(string name, Action<string> warn)
        {
            if (string.IsNullOrEmpty(name)) return KeyCode.BackQuote;
            string text = name.Trim();

            // 配置里直接写按键符号也认，省得用户查 Unity 枚举名。
            if (text == "`" || text == "~" || text == "·") return KeyCode.BackQuote;
            if (text == "Tab") return KeyCode.Tab;
            if (text == "Escape" || text == "Esc") return KeyCode.Escape;
            if (text == "Space" || text == "空格") return KeyCode.Space;

            KeyCode parsed;
            if (Enum.TryParse(text, true, out parsed) && parsed != KeyCode.None) return parsed;

            if (warn != null) warn("toggleKey 无法识别: " + name + "，改用 BackQuote");
            return KeyCode.BackQuote;
        }

        private static HudPressMode ParseMode(string value, Action<string> warn)
        {
            if (string.IsNullOrEmpty(value)) return HudPressMode.Hold;
            string text = value.Trim();
            if (string.Equals(text, "Hold", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "LongPress", StringComparison.OrdinalIgnoreCase) ||
                text == "长按") return HudPressMode.Hold;
            if (string.Equals(text, "Toggle", StringComparison.OrdinalIgnoreCase) ||
                text == "切换") return HudPressMode.Toggle;

            if (warn != null) warn("pressMode 无法识别: " + value + "，改用 Hold");
            return HudPressMode.Hold;
        }

        private static float ClampHoldSeconds(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return DefaultHoldSeconds;
            return Math.Max(0.1f, Math.Min(2f, value));
        }
    }
}
