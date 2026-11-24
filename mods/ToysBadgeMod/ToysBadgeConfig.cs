using System;
using CesiumLoader.SDK.Configuration;

namespace ToysBadgeMod
{
    internal sealed class ToysBadgeConfig
    {
        public bool Enabled;
        public string ServiceUrl;
        public int RefreshSeconds;
        public int TimeoutSeconds;
        public int IconSize;
        public int Gap;
        public int OffsetX;
        public int OffsetY;
        public bool ShowSelfBadge;
        public bool ShowOfflineMembers;
        public bool ShareChatClientId;
        public string IconPath;
        public string ClientId;

        public ToysBadgeConfig()
        {
            Enabled = true;
            ServiceUrl = "https://connect.hiynet.com";
            RefreshSeconds = 25;
            TimeoutSeconds = 15;
            IconSize = 26;
            Gap = 4;
            OffsetX = 0;
            OffsetY = 0;
            ShowSelfBadge = true;
            ShowOfflineMembers = false;
            ShareChatClientId = true;
            IconPath = "";
            ClientId = "";
        }

        public static void EnsureDefaults(ModConfig cfg)
        {
            if (cfg == null) return;

            cfg.SetDefault("Enabled", true);
            cfg.SetDefault("ServiceUrl", "https://connect.hiynet.com");
            cfg.SetDefault("RefreshSeconds", 25);
            cfg.SetDefault("TimeoutSeconds", 15);
            cfg.SetDefault("IconSize", 26);
            cfg.SetDefault("Gap", 4);
            cfg.SetDefault("OffsetX", 0);
            cfg.SetDefault("OffsetY", 0);
            cfg.SetDefault("ShowSelfBadge", true);
            cfg.SetDefault("ShowOfflineMembers", false);
            cfg.SetDefault("ShareChatClientId", true);
            cfg.SetDefault("IconPath", "");

            string existingClientId = cfg.GetString("ClientId", "");
            if (!PresenceProtocol.IsValidClientId(existingClientId))
            {
                cfg.Set("ClientId", Guid.NewGuid().ToString("N"));
            }

            ToysBadgeConfigDocs.Apply(cfg);
        }

        public static ToysBadgeConfig Read(ModConfig cfg)
        {
            ToysBadgeConfig res = new ToysBadgeConfig();
            if (cfg == null) return res;

            res.Enabled = cfg.GetBool("Enabled", true);
            res.ServiceUrl = NormalizeServiceUrl(cfg.GetString("ServiceUrl", "https://connect.hiynet.com"));
            res.RefreshSeconds = Clamp(cfg.GetInt("RefreshSeconds", 25), 15, 55);
            res.TimeoutSeconds = Clamp(cfg.GetInt("TimeoutSeconds", 15), 5, 60);
            res.IconSize = Clamp(cfg.GetInt("IconSize", 26), 14, 64);
            res.Gap = Clamp(cfg.GetInt("Gap", 4), -32, 48);
            res.OffsetX = Clamp(cfg.GetInt("OffsetX", 0), -200, 200);
            res.OffsetY = Clamp(cfg.GetInt("OffsetY", 0), -200, 200);
            res.ShowSelfBadge = cfg.GetBool("ShowSelfBadge", true);
            res.ShowOfflineMembers = cfg.GetBool("ShowOfflineMembers", false);
            res.ShareChatClientId = cfg.GetBool("ShareChatClientId", true);
            res.IconPath = (cfg.GetString("IconPath", "") ?? "").Trim();

            string cid = (cfg.GetString("ClientId", "") ?? "").Trim();
            if (!PresenceProtocol.IsValidClientId(cid))
            {
                cid = Guid.NewGuid().ToString("N");
                cfg.Set("ClientId", cid);
                cfg.SaveIfDirty();
            }
            res.ClientId = cid;

            return res;
        }

        public bool SameConnectionSettings(ToysBadgeConfig other)
        {
            if (other == null) return false;
            return Enabled == other.Enabled
                && string.Equals(ServiceUrl, other.ServiceUrl, StringComparison.Ordinal)
                && RefreshSeconds == other.RefreshSeconds
                && TimeoutSeconds == other.TimeoutSeconds
                && ShareChatClientId == other.ShareChatClientId
                && string.Equals(ClientId, other.ClientId, StringComparison.Ordinal);
        }

        private static string NormalizeServiceUrl(string raw)
        {
            string s = (raw ?? "").Trim();
            while (s.EndsWith("/"))
            {
                s = s.Substring(0, s.Length - 1);
            }
            return s;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
