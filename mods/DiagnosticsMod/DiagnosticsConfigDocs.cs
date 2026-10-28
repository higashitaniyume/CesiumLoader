using CesiumLoader.SDK.Configuration;

namespace DiagnosticsMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class DiagnosticsConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("dumpAllKey", D(
                "导出全部诊断信息的按键，默认 F10。",
                "匯出全部診斷資訊的按鍵，預設 F10。",
                "Key that dumps all diagnostics (default F10).",
                "すべての診断情報を出力するキー（既定 F10）。"));
            changed |= config.Describe("dumpCameraKey", D(
                "只导出相机信息的按键，默认 F11。",
                "只匯出相機資訊的按鍵，預設 F11。",
                "Key that dumps camera information only (default F11).",
                "カメラ情報だけを出力するキー（既定 F11）。"));
            changed |= config.Describe("dumpSceneKey", D(
                "只导出场景信息的按键，默认 F12。",
                "只匯出場景資訊的按鍵，預設 F12。",
                "Key that dumps scene information only (default F12).",
                "シーン情報だけを出力するキー（既定 F12）。"));
            changed |= config.Describe("dumpSceneOnChange", D(
                "每次加载场景时自动导出一份诊断信息。",
                "每次載入場景時自動匯出一份診斷資訊。",
                "Automatically dump diagnostics whenever a scene loads.",
                "シーン読み込み時に自動で診断情報を出力します。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
