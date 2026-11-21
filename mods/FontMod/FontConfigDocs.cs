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
                "要替换成的系统字体家族名（含用户目录安装的字体，例如 Microsoft YaHei）。留空 = 使用游戏默认字体；一般由 Toys「自定义 → 字体」写入。",
                "要替換成的系統字型家族名稱（含使用者目錄安裝的字型）。留空 = 使用遊戲預設字型；通常由 Toys「自訂 → 字型」寫入。",
                "System or user-installed font family name (e.g. Microsoft YaHei). Empty = keep game default; normally written by Toys (Customize → Font).",
                "使用するシステム／ユーザーフォントのファミリー名。空 = ゲーム既定のフォント。通常は Toys の「カスタマイズ → フォント」から設定します。"));
            changed |= config.Describe("applyToAllFonts", D(
                "true = 替换全部界面字体（含标题与数字字体）；false = 只替换正文，保留游戏原有的标题与数字装饰字体。",
                "true = 替換全部介面字型（含標題與數字字型）；false = 只替換正文，保留遊戲原本的標題與數字裝飾字型。",
                "true = replace every UI font including decorative headings and numbers; false = replace body fonts only.",
                "true = 見出し・数字を含むすべての UI フォントを置換。false = 本文のみ置換し、見出しと数字はゲーム既定のまま。"));
            changed |= config.Describe("fallbackFonts", D(
                "逗号分隔的回退字体，用于同图集 SDF 补字以及旧版动态字体缺字回退。",
                "逗號分隔的回退字型，用於同圖集 SDF 補字以及舊版動態字型缺字回退。",
                "Comma-separated fallback fonts used for same-atlas SDF glyph fill and legacy font fallback.",
                "カンマ区切りのフォールバックフォント。同一アトラス SDF 補完と旧フォントのフォールバックに使用されます。"));
            changed |= config.Describe("checkCoverage", D(
                "仅关闭 bridgeDynamicToSdf 走旧版替换路径时生效：检查游戏文案缺字过多时保持默认字体。",
                "僅關閉 bridgeDynamicToSdf 走舊版替換路徑時生效：檢查遊戲文案缺字過多時保持預設字型。",
                "Applies only when bridgeDynamicToSdf is false (legacy path): blocks fonts missing too many game glyphs.",
                "bridgeDynamicToSdf=false の旧経路でのみ有効：欠け字が多いフォントをブロックします。"));
            changed |= config.Describe("fontPath", D(
                "可选：直接指定本地 .ttf/.otf/.ttc 字体文件完整路径（无需安装到系统；填了此项后优先级高于 fontFamily）。",
                "可選：直接指定本機 .ttf/.otf/.ttc 字型檔案完整路徑（無需安裝到系統；填寫後優先權高於 fontFamily）。",
                "Optional direct path to a local .ttf/.otf/.ttc file (no OS install required; overrides fontFamily when set).",
                "任意：ローカルの .ttf/.otf/.ttc ファイルパスを直接指定（OS インストール不要。指定時は fontFamily より優先）。"));
            changed |= config.Describe("fontStyle", D(
                "可选字重名（如 Regular、Bold、Light），通常留空自动识别即可。",
                "可選字重名（如 Regular、Bold、Light），通常留空自動識別即可。",
                "Optional font style/weight hint (e.g. Regular, Bold, Light); leave empty for auto-detection.",
                "任意のスタイル／ウェイト名（Regular / Bold / Light など）。通常は空のままで自動判定されます。"));
            changed |= config.Describe("titleFontPath", D(
                "可选：单独为标题/数字装饰字体（SHOWG/hp02/dgo/Impact 等）指定另一套字体文件路径，留空则与正文共用主字体。",
                "可選：單獨為標題/數字裝飾字型指定另一套字型檔案路徑，留空則與正文共用主字型。",
                "Optional separate font file path for decorative title/number fonts; empty shares the main font.",
                "任意：見出し・数字用装飾フォントに別フォントファイルを指定。空なら本文と共通。"));
            changed |= config.Describe("replaceTmpFonts", D(
                "true = 动态生成 SDF 图集替换所有 _TMP 字体（含主力字体 JingNanBoBoHei_TMP）。",
                "true = 動態產生 SDF 圖集替換所有 _TMP 字型（含主力字型 JingNanBoBoHei_TMP）。",
                "true = dynamically generate SDF atlas to replace all _TMP fonts (including JingNanBoBoHei_TMP).",
                "true = 動的 SDF アトラスを生成してすべての _TMP フォント（JingNanBoBoHei_TMP を含む）を置換。"));
            changed |= config.Describe("bridgeDynamicToSdf", D(
                "true = 将旧版 DynamicFont 统一桥接到高清动态 SDF 图集（兼容用户目录与未安装字体，彻底消除缺字与粗边问题）；false = 退回 v1.0 旧版替换逻辑。",
                "true = 將舊版 DynamicFont 統一橋接到高清動態 SDF 圖集（相容使用者目錄與未安裝字型）；false = 退回 v1.0 舊版替換邏輯。",
                "true = bridge legacy DynamicFonts onto the dynamic SDF atlas (works with user/local font files); false = use v1.0 legacy replacement.",
                "true = 旧 DynamicFont も動的 SDF アトラスへ統一ブリッジ（ユーザーフォント対応）；false = v1.0 の旧方式を使用。"));
            changed |= config.Describe("replaceSceneTmpText", D(
                "是否顺带替换场景中的原生 3D TMPro.TMP_Text 文本。",
                "是否順帶替換場景中的原生 3D TMPro.TMP_Text 文字。",
                "Whether to also replace active 3D scene TMPro.TMP_Text components.",
                "シーン内のネイティブ 3D TMPro.TMP_Text も併せて置換するか。"));
            changed |= config.Describe("atlasSize", D(
                "动态 SDF 图集宽高（1024~8192，默认 4096，单张 Alpha8 约 16MB，可容纳 5000+ 汉字）。",
                "動態 SDF 圖集寬高（1024~8192，預設 4096，單張 Alpha8 約 16MB）。",
                "Dynamic SDF atlas dimension (1024–8192, default 4096; ~16 MB Alpha8 holding 5000+ CJK glyphs).",
                "動的 SDF アトラスのサイズ（1024〜8192、既定 4096）。"));
            changed |= config.Describe("samplingPointSize", D(
                "SDF 采样字号（16~96，默认 48，兼顾锐度与单图集容量）。",
                "SDF 取樣字號（16~96，預設 48，兼顧銳度與單圖集容量）。",
                "SDF sampling point size (16–96, default 48).",
                "SDF サンプリングポイントサイズ（16〜96、既定 48）。"));
            changed |= config.Describe("atlasPadding", D(
                "SDF 扩散边距（2~16，默认 5；越小描边越细、字形主体越清晰）。",
                "SDF 擴散邊距（2~16，預設 5；越小描邊越細、字形主體越清晰）。",
                "SDF atlas padding (2–16, default 5; smaller values produce crisper, thinner outlines).",
                "SDF アトラスパディング（2〜16、既定 5。小さいほど輪郭が細く字形が明瞭になります）。"));
            changed |= config.Describe("tmpFontScale", D(
                "SDF 字体视觉缩放微调（0.5~2.0，默认 1.0）。",
                "SDF 字型視覺縮放微調（0.5~2.0，預設 1.0）。",
                "Visual scale multiplier for SDF fonts (0.5–2.0, default 1.0).",
                "SDF フォントの見た目スケール倍率（0.5〜2.0、既定 1.0）。"));
            changed |= config.Describe("normalWeight", D(
                "常规文字的 SDF 字重微调（-0.5~0.5，默认 0.0；粗黑类字体觉得偏厚可调如 -0.05）。",
                "常規文字的 SDF 字重微調（-0.5~0.5，預設 0.0）。",
                "SDF normal text weight tweak (-0.5 to 0.5, default 0.0).",
                "通常文字の SDF ウェイト微調整（-0.5〜0.5、既定 0.0）。"));
            changed |= config.Describe("boldWeight", D(
                "粗体文字的 SDF 加粗幅度（0.0~1.0，默认 0.15；设为 0.0 可关闭粗体膨胀）。",
                "粗體文字的 SDF 加粗幅度（0.0~1.0，預設 0.15；設為 0.0 可關閉粗體膨脹）。",
                "SDF bold text weight expansion (0.0 to 1.0, default 0.15; set 0.0 to disable faux bold dilation).",
                "太字の SDF 膨張幅（0.0〜1.0、既定 0.15。0.0 で無効化）。"));
            changed |= config.Describe("warmupGameCharset", D(
                "启动替换时将游戏常用汉字表预烘焙进动态 SDF 图集并自动补齐缺字。",
                "啟動替換時將遊戲常用漢字表預烘焙進動態 SDF 圖集並自動補齊缺字。",
                "Pre-bake the game's common CJK charset and same-atlas fallback glyphs on apply.",
                "適用時にゲーム常用文字セットと同一アトラス補完字形を事前生成します。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
        {
            return ConfigDoc.Of(zhHans, zhHant, en, ja);
        }
    }
}
