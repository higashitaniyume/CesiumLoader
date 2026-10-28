using CesiumLoader.SDK.Configuration;

namespace ForkDirectionMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class ForkDirectionConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：在 PVE 三岔路按邻接顺序标记相邻地块。",
                "總開關：在 PVE 三岔路按相鄰順序標記相鄰地塊。",
                "Master switch: label adjacent lands at PVE forks in adjacency order.",
                "全体スイッチ：PVE の分岐路で隣接地塊を隣接順に表示します。"));
            changed |= config.Describe("fontSize", D(
                "标记文字字号，范围 14–48，默认 24。",
                "標記文字字級，範圍 14–48，預設 24。",
                "Marker font size (14–48, default 24).",
                "マーカーの文字サイズ（14～48、既定 24）。"));
            changed |= config.Describe("heightOffset", D(
                "标记相对地块的高度偏移（米），范围 0–10，默认 0.8。",
                "標記相對地塊的高度偏移（公尺），範圍 0–10，預設 0.8。",
                "Marker height offset above the land in meters (0–10, default 0.8).",
                "地塊からのマーカー高さオフセット（メートル、0～10、既定 0.8）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
