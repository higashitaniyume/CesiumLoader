using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CesiumLoader.Overlay
{
    public sealed class OverlayConfig
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("scale")] public double Scale { get; set; } = 1.0;
        [JsonPropertyName("fontSize")] public int FontSize { get; set; } = 14;
        [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.85;
        [JsonPropertyName("toggleKey")] public string ToggleKey { get; set; } = "Backslash";
        [JsonPropertyName("toggleVk")] public int ToggleVk { get; set; } = 0xDC;
        [JsonPropertyName("offsetX")] public double OffsetX { get; set; } = 20;
        [JsonPropertyName("offsetY")] public double OffsetY { get; set; } = 70;
        [JsonPropertyName("panelWidth")] public double PanelWidth { get; set; } = 340;

        public static OverlayConfig Load(string path, out string fullPath)
        {
            string dir = AppContext.BaseDirectory;
            string candidate = !string.IsNullOrEmpty(path) ? path : Path.Combine(dir, "overlay_config.json");
            // loader 注入的 CESIUM_OVERLAY_CFG 直接指向 AstralParty_ModLoader/overlay/overlay_config.json
            string env = Environment.GetEnvironmentVariable("CESIUM_OVERLAY_CFG");
            if (!string.IsNullOrEmpty(env)) candidate = env;
            // 兜底：exe 同级
            if (!File.Exists(candidate))
            {
                string alt = Path.Combine(dir, "overlay_config.json");
                if (File.Exists(alt)) candidate = alt;
            }
            fullPath = candidate;
            if (!File.Exists(candidate)) return new OverlayConfig();
            try
            {
                string json = File.ReadAllText(candidate);
                var cfg = JsonSerializer.Deserialize<OverlayConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg != null)
                {
                    //  clamp
                    if (cfg.Scale <= 0) cfg.Scale = 1.0;
                    if (cfg.FontSize < 10 || cfg.FontSize > 36) cfg.FontSize = 14;
                    if (cfg.Opacity < 0.2 || cfg.Opacity > 1) cfg.Opacity = 0.85;
                    if (string.IsNullOrEmpty(cfg.ToggleKey)) cfg.ToggleKey = "Backslash";
                    // ToggleKey 与 ToggleVk 两个字段互认：以 ToggleKey 文本为准派生 VK
                    int vk = KeyNameToVk(cfg.ToggleKey);
                    if (vk > 0) cfg.ToggleVk = vk;
                    if (cfg.ToggleVk <= 0) cfg.ToggleVk = 0xDC;
                }
                return cfg ?? new OverlayConfig();
            }
            catch { return new OverlayConfig(); }
        }

        public async Task SaveAsync(string fullPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath)) fullPath = Path.Combine(AppContext.BaseDirectory, "overlay_config.json");
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                // 原子写：先写 tmp 再 Move
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                string tmp = fullPath + ".tmp";
                await File.WriteAllTextAsync(tmp, json);
                File.Move(tmp, fullPath, true);
            }
            catch { }
        }

        public static int KeyNameToVk(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            string n = name.Trim();
            if (string.Equals(n, "Backslash", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "Oem5", StringComparison.OrdinalIgnoreCase) || n == "\\") return 0xDC;
            if (string.Equals(n, "Insert", StringComparison.OrdinalIgnoreCase)) return 0x2D;
            // Unity KeyCode 名字到 VK 的模糊映射，Toys 页面只给这几种
            switch (n.ToLowerInvariant())
            {
                case "backslash": return 0xDC;
                case "insert": return 0x2D;
                case "delete": return 0x2E;
                case "home": return 0x24;
                case "end": return 0x23;
                default: return 0xDC; // 其它未知一律按反斜杠，失败不致命
            }
        }

        public static string VkToKeyName(int vk)
        {
            switch (vk)
            {
                case 0xDC: return "Backslash";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                default: return "Backslash";
            }
        }
    }
}
