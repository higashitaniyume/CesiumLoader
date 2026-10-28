using CesiumLoader.SDK.Configuration;

namespace CardSkinMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class CardSkinConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("Enabled", D(
                "总开关：关闭后使用游戏官方卡面。",
                "總開關：關閉後使用遊戲官方卡面。",
                "Master switch: use the game's official card art when off.",
                "全体スイッチ：オフにするとゲーム公式のカード画像を使います。"));
            changed |= config.Describe("ActiveSkin", D(
                "当前启用的卡面皮肤方案目录名，默认 default。",
                "目前啟用的卡面面板方案目錄名稱，預設 default。",
                "Folder name of the active card skin scheme (default: default).",
                "有効なカードスキン方案のフォルダー名（既定: default）。"));
            changed |= config.Describe("ForceFullCard", D(
                "强制静态卡面使用全图异画布局；不影响游戏原生异画或视频。",
                "強制靜態卡面使用全圖異畫版面；不影響遊戲原生異畫或影片。",
                "Force static cards into the full-art layout; native alternate art and videos are unaffected.",
                "静的カードを全面イラスト表示にします（ゲーム本来の異画・動画には影響しません）。"));
            changed |= config.Describe("AnimationCacheMiB", D(
                "动态卡面纹理缓存上限（MiB），范围 64–1024，默认 512；手动添加该键后生效。",
                "動態卡面材質快取上限（MiB），範圍 64–1024，預設 512；手動加入此鍵後生效。",
                "Texture cache limit for animated cards in MiB (64–1024, default 512); effective after adding this key manually.",
                "アニメーションカードのテクスチャキャッシュ上限（MiB、64～1024、既定 512）。手動でこのキーを追加すると有効になります。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
