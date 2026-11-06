using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CesiumLoader.Overlay
{
    public sealed class PeekMessage
    {
        [JsonPropertyName("v")] public int Version { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("panel")] public string Panel { get; set; } // shop | relic
        [JsonPropertyName("visible")] public bool Visible { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; }
        [JsonPropertyName("fontSize")] public int FontSize { get; set; } = 14;
        [JsonPropertyName("panelWidth")] public double PanelWidth { get; set; } = 340;
        [JsonPropertyName("offsetX")] public double OffsetX { get; set; } = 20;
        [JsonPropertyName("offsetY")] public double OffsetY { get; set; } = 70;
        [JsonPropertyName("ts")] public long Ts { get; set; }
    }

    public static class PeekProtocol
    {
        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        public static bool TryParse(byte[] data, int len, out PeekMessage msg)
        {
            msg = null;
            if (data == null || len <= 0) return false;
            try
            {
                string json = System.Text.Encoding.UTF8.GetString(data, 0, len);
                msg = JsonSerializer.Deserialize<PeekMessage>(json, Opts);
                if (msg == null || msg.Kind != "peek") return false;
                if (msg.Panel != "shop" && msg.Panel != "relic") return false;
                if (msg.FontSize <= 0) msg.FontSize = 14;
                if (msg.PanelWidth < 200) msg.PanelWidth = 340;
                if (msg.PanelWidth > 800) msg.PanelWidth = 800;
                return true;
            }
            catch { return false; }
        }
    }
}
