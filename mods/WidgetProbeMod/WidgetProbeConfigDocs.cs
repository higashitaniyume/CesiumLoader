using CesiumLoader.SDK.Configuration;

namespace WidgetProbeMod
{
    /// <summary>config.json 各字段的多语言说明。</summary>
    internal static class WidgetProbeConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：是否显示观测面板。",
                "總開關：是否顯示觀測面板。",
                "Master switch: show/hide the observation panel.",
                "マスタースイッチ: 観測パネルを表示します。"));
            changed |= config.Describe("toggleKey", D(
                "显示/隐藏观测面板的热键，默认 F9。",
                "顯示/隱藏觀測面板的熱鍵，預設 F9。",
                "Hotkey to show/hide the observation panel (default F9).",
                "観測パネル表示/非表示のホットキー（既定値 F9）。"));
            changed |= config.Describe("clearKey", D(
                "清空事件流水与计数（用于分段验证）的热键，默认 F8。",
                "清空事件流水與計數（用於分段驗證）的熱鍵，預設 F8。",
                "Hotkey to clear the event feed and counters (default F8).",
                "イベントログとカウンターをクリアするホットキー（既定値 F8）。"));
            changed |= config.Describe("fontSize", D(
                "面板正文字号，范围 12–20，默认 14。",
                "面板正文字號，範圍 12–20，預設 14。",
                "Panel body font size (12-20, default 14).",
                "パネル本文フォントサイズ（12–20、既定値 14）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
