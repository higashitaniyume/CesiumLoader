using CesiumLoader.SDK.Configuration;

namespace OverlayFeedMod
{
    internal static class OverlayFeedConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：是否把商店/筹码历史推给 WPF 覆盖层。",
                "總開關：是否把商店/籌碼歷史推給 WPF 覆蓋層。",
                "Master switch: feed shop/relic history to the WPF overlay.",
                "全体スイッチ：ショップ/チップ履歴を WPF オーバーレイに送信します。"));
            changed |= config.Describe("showShopCards", D(
                "是否推送商店看板数据。",
                "是否推送商店看板數據。",
                "Whether to feed shop panel data.",
                "ショップパネルデータを送信するかどうか。"));
            changed |= config.Describe("showRelicCandidates", D(
                "是否推送筹码看板数据。",
                "是否推送籌碼看板數據。",
                "Whether to feed relic panel data.",
                "チップパネルデータを送信するかどうか。"));
            changed |= config.Describe("shopDisplayDurationSeconds", D(
                "商店数据保留秒数。",
                "商店數據保留秒數。",
                "Seconds to keep shop data after leaving (default 20).",
                "退店後ショップデータを保持する秒数（既定 20）。"));
            changed |= config.Describe("relicDisplayDurationSeconds", D(
                "筹码数据保留秒数。",
                "籌碼數據保留秒數。",
                "Seconds to keep relic data after selection (default 20).",
                "決定後チップデータを保持する秒数（既定 20）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
