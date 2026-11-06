namespace OverlayFeedMod
{
    public sealed class OverlayFeedConfig
    {
        public bool Enabled { get; set; } = true;
        public bool ShowFloorTag { get; set; } = true;
        public bool ShowShopCards { get; set; } = true;
        public bool ShowRelicCandidates { get; set; } = true;
        public int ShopDisplayDurationSeconds { get; set; } = 20;
        public int RelicDisplayDurationSeconds { get; set; } = 20;
        public bool ColorRelicByQuality { get; set; } = true;

        public static int Clamp(int value, int min, int max, int fallback)
        {
            if (value < min || value > max) return fallback;
            return value;
        }
    }
}
