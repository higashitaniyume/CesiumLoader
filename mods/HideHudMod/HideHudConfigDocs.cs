using CesiumLoader.SDK.Configuration;

namespace HideHudMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class HideHudConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：关闭后不响应隐藏 HUD 的热键。",
                "總開關：關閉後不回應隱藏 HUD 的熱鍵。",
                "Master switch: disables the hide-HUD hotkey when off.",
                "全体スイッチ：オフにすると HUD 非表示のホットキーに反応しません。"));
            changed |= config.Describe("toggleKey", D(
                "隐藏 HUD 的按键，默认 BackQuote（Tab 上方）。",
                "隱藏 HUD 的按鍵，預設 BackQuote（Tab 上方）。",
                "Key that hides the HUD (default: BackQuote, above Tab).",
                "HUD を隠すキー（既定: BackQuote、Tab の上）。"));
            changed |= config.Describe("pressMode", D(
                "按键方式：Hold 长按隐藏、松开恢复；Toggle 按一下切换。",
                "按鍵方式：Hold 長按隱藏、放開恢復；Toggle 按一下切換。",
                "Key mode: Hold hides while held; Toggle switches on each press.",
                "キー動作：Hold は押している間だけ非表示、Toggle は押すたびに切り替え。"));
            changed |= config.Describe("holdSeconds", D(
                "Hold 模式需要按住的秒数，默认 0.35。",
                "Hold 模式需要按住的秒數，預設 0.35。",
                "Seconds the key must be held in Hold mode (default 0.35).",
                "Hold モードで押し続ける秒数（既定 0.35）。"));
            changed |= config.Describe("hideAllHud", D(
                "一次隐藏全部 HUD，忽略下面的分项开关。",
                "一次隱藏全部 HUD，忽略下方的分項開關。",
                "Hide the entire HUD at once, ignoring the individual toggles below.",
                "HUD をまとめて非表示にします（下の個別スイッチは無視）。"));
            changed |= config.Describe("hideHandCards", D(
                "隐藏手牌区域。",
                "隱藏手牌區域。",
                "Hide the hand area.",
                "手札エリアを隠します。"));
            changed |= config.Describe("hideMonsterDisplay", D(
                "隐藏怪物信息条。",
                "隱藏怪物資訊條。",
                "Hide the monster info bar.",
                "モンスター情報バーを隠します。"));
            changed |= config.Describe("hideAvatars", D(
                "隐藏玩家头像。",
                "隱藏玩家頭像。",
                "Hide player avatars.",
                "プレイヤーのアイコンを隠します。"));
            changed |= config.Describe("hideOther", D(
                "隐藏其他 HUD 元素。",
                "隱藏其他 HUD 元素。",
                "Hide other HUD elements.",
                "その他の HUD 要素を隠します。"));
            changed |= config.Describe("notify", D(
                "显示隐藏/恢复 HUD 的屏幕提示，默认关闭。",
                "顯示隱藏/恢復 HUD 的螢幕提示，預設關閉。",
                "Show an on-screen notice when hiding or restoring the HUD (off by default).",
                "HUD の非表示/復帰時に画面へ通知を表示します（既定オフ）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
