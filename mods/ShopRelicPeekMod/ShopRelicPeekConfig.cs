namespace ShopRelicPeekMod
{
    public sealed class ShopRelicPeekConfig
    {
        public bool Enabled { get; set; } = true;
        public int FontSize { get; set; } = 18;
        public bool ShowFloorTag { get; set; } = true;
        public string ClearKey { get; set; } = "T";

        // 商店独立看板设置
        public bool ShowShopCards { get; set; } = true;
        public float ShopPanelWidth { get; set; } = 340f;
        public float ShopOffsetX { get; set; } = 20f;
        public float ShopOffsetY { get; set; } = 70f;
        public int ShopDisplayDurationSeconds { get; set; } = 20;

        // 筹码独立看板设置
        public bool ShowRelicCandidates { get; set; } = true;
        public float RelicPanelWidth { get; set; } = 340f;
        public float RelicOffsetX { get; set; } = 20f;
        public float RelicOffsetY { get; set; } = 320f;
        public int RelicDisplayDurationSeconds { get; set; } = 20;
        public bool ColorRelicByQuality { get; set; } = true;

        public static int Clamp(int value, int min, int max, int fallback)
        {
            if (value < min || value > max) return fallback;
            return value;
        }

        public static float Clamp(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max) return fallback;
            return value;
        }
    }
}
