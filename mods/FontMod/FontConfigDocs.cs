using CesiumLoader.SDK.Configuration;

namespace FontMod
{
    /// <summary>config.json 里各字段的多语言说明（Toys 配置弹窗按界面语言显示）。</summary>
    internal static class FontConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：关闭后立即恢复游戏默认字体。",
                "總開關：關閉後立即恢復遊戲預設字型。",
                "Master switch: restores the game's default font immediately when off.",
                "全体スイッチ：オフにするとゲーム既定のフォントに戻ります。"));
            changed |= config.Describe("fontFamily", D(
                "要替换成的系统字体家族名（例如 Microsoft YaHei）。留空 = 使用游戏默认字体；一般由 Toys「自定义 → 字体」写入，不需要手改。",
                "要替換成的系統字型家族名稱（例如 Microsoft YaHei）。留空 = 使用遊戲預設字型；通常由 Toys「自訂 → 字型」寫入，不需要手改。",
                "System font family to use (for example Microsoft YaHei). Empty = keep the game default; normally written by Toys (Customize → Font).",
                "使用するシステムフォントのファミリー名（例: Microsoft YaHei）。空 = ゲーム既定のフォント。通常は Toys の「カスタマイズ → フォント」から設定します。"));
            changed |= config.Describe("applyToAllFonts", D(
                "true = 替换所有界面旧版字体（含标题/数字）；false = 只替换默认正文，保留游戏原有的标题与数字字体。",
                "true = 替換所有介面舊版字型（含標題/數字）；false = 只替換預設正文，保留遊戲原本的標題與數字字型。",
                "true = replace every legacy UI font (including headings and numbers); false = only replace the default body font.",
                "true = 旧来の UI フォントをすべて置換（見出し・数字を含む）。false = 本文のみ置換し、見出しと数字はゲーム既定のまま。"));
            changed |= config.Describe("fallbackFonts", D(
                "逗号分隔的回退字体，按顺序取第一个包含该字符的字体，用于补用户字体缺少的字。",
                "逗號分隔的回退字型，依序取第一個包含該字元的字型，用來補使用者字型缺少的字。",
                "Comma-separated fallback fonts; the first one containing a character is used when the chosen font lacks it.",
                "カンマ区切りのフォールバックフォント。選択したフォントに無い文字は、その文字を含む最初のフォントで表示します。"));
            changed |= config.Describe("checkCoverage", D(
                "替换前用游戏文案的字符集检查缺字；缺字太多时保持游戏默认字体，避免整屏方块。确认字体没问题但被拦下时可设为 false 强制应用。",
                "替換前用遊戲文案的字元集檢查缺字；缺字太多時保持遊戲預設字型，避免整頁方塊。確認字型沒問題但被攔下時可設為 false 強制套用。",
                "Before applying, checks missing glyphs against the game's text corpus; keeps the default font when too many characters are missing. Set to false to force-apply a font that was blocked.",
                "置換前にゲーム文章の文字集合で欠け字を確認します。欠け字が多い場合はゲーム既定のフォントを維持して文字化けを防ぎます。問題ないのにブロックされる場合のみ false にしてください。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
