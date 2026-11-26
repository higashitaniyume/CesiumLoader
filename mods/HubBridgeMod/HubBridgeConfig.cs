using System;
using CesiumLoader.SDK.Configuration;

namespace HubBridgeMod
{
    /// <summary>
    /// 模组配置项的读取与合法范围夹取。
    /// </summary>
    internal sealed class HubBridgeConfig
    {
        public bool Enabled;
        public int PollIntervalMs;
        public int RequestTimeoutSeconds;
        public string EndpointPath;

        public HubBridgeConfig()
        {
            Enabled = true;
            PollIntervalMs = 250;
            RequestTimeoutSeconds = 5;
            EndpointPath = "";
        }

        public static void EnsureDefaults(ModConfig cfg)
        {
            if (cfg == null) return;

            cfg.SetDefault("Enabled", true);
            cfg.SetDefault("PollIntervalMs", 250);
            cfg.SetDefault("RequestTimeoutSeconds", 5);
            cfg.SetDefault("EndpointPath", "");
            HubBridgeConfigDocs.Apply(cfg);
        }

        public static HubBridgeConfig Read(ModConfig cfg)
        {
            var res = new HubBridgeConfig();
            if (cfg == null) return res;

            res.Enabled = cfg.GetBool("Enabled", true);
            res.PollIntervalMs = Clamp(cfg.GetInt("PollIntervalMs", 250), 100, 2000);
            res.RequestTimeoutSeconds = Clamp(cfg.GetInt("RequestTimeoutSeconds", 5), 2, 30);
            res.EndpointPath = (cfg.GetString("EndpointPath", "") ?? "").Trim();
            return res;
        }

        public bool SameSettings(HubBridgeConfig other)
        {
            if (other == null) return false;
            return Enabled == other.Enabled
                && PollIntervalMs == other.PollIntervalMs
                && RequestTimeoutSeconds == other.RequestTimeoutSeconds
                && string.Equals(EndpointPath, other.EndpointPath, StringComparison.Ordinal);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
