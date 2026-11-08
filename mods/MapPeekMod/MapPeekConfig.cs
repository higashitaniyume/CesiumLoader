namespace MapPeekMod
{
    public sealed class MapPeekConfig
    {
        public bool Enabled { get; set; } = true;
        public int FontSize { get; set; } = 18;
        public float PanelWidth { get; set; } = 340f;
        public float OffsetX { get; set; } = 20f;
        public float OffsetY { get; set; } = 70f;
        public bool ShowMapId { get; set; } = true;
        public bool ShowMode { get; set; } = true;
        public bool ShowDifficulty { get; set; } = true;
        public bool ShowPlayerCount { get; set; } = true;

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
