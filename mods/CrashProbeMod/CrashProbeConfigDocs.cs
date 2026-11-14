using CesiumLoader.SDK.Configuration;

namespace CrashProbeMod
{
    /// <summary>config.json 里各字段的多语言说明(Toys 与离线编辑器据此显示注释)。</summary>
    internal static class CrashProbeConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：关闭后本模组不做任何事（不会崩溃）。",
                "總開關：關閉後本模組不做任何事（不會崩潰）。",
                "Master switch: when off, the probe does nothing (no crash).",
                "全体スイッチ：オフにすると何も行いません（クラッシュしません）。"));
            changed |= config.Describe("triggerOnSettings", D(
                "打开设置页时触发。关闭后只能用热键手动触发。",
                "開啟設定頁時觸發。關閉後只能用熱鍵手動觸發。",
                "Trigger when the settings window opens. When off, only the hotkey triggers it.",
                "設定画面を開いたときに発動。オフにするとホットキーのみで発動します。"));
            changed |= config.Describe("delayMs", D(
                "触发延迟（毫秒，0–60000）：打开设置页后等这么久再崩，便于看到界面与日志。",
                "觸發延遲（毫秒，0–60000）：開啟設定頁後等這麼久再崩，便於看到介面與日誌。",
                "Delay in milliseconds (0–60000) between the trigger and the crash.",
                "発動までの遅延（ミリ秒、0～60000）。"));
            changed |= config.Describe("hotkey", D(
                "手动触发热键（如 F8；填 none 表示不绑定）。设置页判定失效时的兜底入口。",
                "手動觸發熱鍵（如 F8；填 none 表示不綁定）。設定頁判定失效時的備援入口。",
                "Manual trigger hotkey (e.g. F8; use none to disable). Fallback when settings detection fails.",
                "手動発動ホットキー（例 F8、none で無効）。設定画面の判定が使えない場合の予備手段。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
