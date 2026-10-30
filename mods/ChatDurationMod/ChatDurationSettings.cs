using CesiumLoader.SDK.Configuration;

namespace ChatDurationMod
{
    /// <summary>config.json 里的业务配置与默认值。</summary>
    public sealed class ChatDurationSettings
    {
        public bool Enabled = true;
        public int DurationSeconds = ChatDurationRules.DefaultSeconds;

        public static void EnsureDefaults(ModConfig config)
        {
            if (config == null) return;
            config.SetDefault("enabled", true);
            config.SetDefault("durationSeconds", ChatDurationRules.DefaultSeconds);
        }

        public static ChatDurationSettings Read(ModConfig config, System.Action<string> warn)
        {
            var settings = new ChatDurationSettings();
            settings.Enabled = config == null || config.GetBool("enabled", true);

            int raw = config != null
                ? config.GetInt("durationSeconds", ChatDurationRules.DefaultSeconds)
                : ChatDurationRules.DefaultSeconds;
            int normalized = ChatDurationRules.NormalizeSeconds(raw);
            settings.DurationSeconds = normalized;

            if (normalized != raw && warn != null)
            {
                warn("durationSeconds=" + raw + " 超出 " + ChatDurationRules.MinSeconds + "–"
                    + ChatDurationRules.MaxSeconds + " 秒，已按 " + normalized + " 秒处理。");
            }
            return settings;
        }
    }
}
