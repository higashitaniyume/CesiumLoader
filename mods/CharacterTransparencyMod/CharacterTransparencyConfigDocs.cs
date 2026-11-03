using CesiumLoader.SDK.Configuration;

namespace CharacterTransparencyMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class CharacterTransparencyConfigDocs
    {
        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
        {
            return ConfigDoc.Of(zhHans, zhHant, en, ja);
        }

        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：关闭后不响应角色半透明热键。",
                "總開關：關閉後不回應角色半透明熱鍵。",
                "Master switch: disables character transparency hotkeys when off.",
                "全体スイッチ：オフにするとキャラクター半透明化ホットキーに反応しません。"));
            changed |= config.Describe("toggleKey", D(
                "角色半透明快捷键，默认 H。",
                "角色半透明快捷鍵，預設 H。",
                "Character transparency key (default: H).",
                "キャラクター半透明化キー（既定: H）。"));
            changed |= config.Describe("holdMode", D(
                "触发模式开关：开启时长按切换（按住半透明、松开恢复），关闭时点击切换（按一下切换开关）。默认开启。",
                "觸發模式開關：開啟市長按切換（按住半透明、放開恢復），關閉時點擊切換（按一下切換開關）。預設開啟。",
                "Trigger mode switch: when enabled, hold to make semi-transparent and release to restore; when disabled, tap to toggle. Enabled by default.",
                "動作モード切り替え：有効時は長押しで半透明・離して復帰、無効時は押すたびに切り替え。既定は有効。"));
            changed |= config.Describe("opacity", D(
                "半透明时的不透明度，范围 0.0 到 1.0（或百分比 0 到 100），默认 0.36（即 36% 不透明度）。",
                "半透明時的不透明度，範圍 0.0 到 1.0（或百分比 0 到 100），預設 0.36（即 36% 不透明度）。",
                "Target opacity when transparent, from 0.0 to 1.0 (or 0 to 100%), default 0.36 (36% opacity).",
                "半透明時の不透明度（0.0～1.0 または 0～100%、既定 0.36 = 36%）。"));
            changed |= config.Describe("holdSeconds", D(
                "长按模式下需要按住的秒数，默认 0.0（立即生效）。",
                "長按模式下需要按住的秒數，預設 0.0（立即生效）。",
                "Seconds the key must be held before activating in hold mode (default: 0.0 for instant).",
                "長押しモードで判定されるまでの秒数（既定 0.0: 即時）。"));
            return changed;
        }
    }
}
