using CesiumLoader.SDK.Configuration;

namespace AngelModeUnlockMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class AngelModeUnlockConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：关闭后模组完全不介入，游戏恢复渠道原始行为（重启游戏生效）。",
                "總開關：關閉後模組完全不介入，遊戲恢復渠道原始行為（重啟遊戲生效）。",
                "Master switch: when off, the mod does nothing and the game keeps its channel's original behavior (takes effect after restart).",
                "全体スイッチ：オフにするとModは一切介入せず、ゲームはチャンネル本来の挙動に戻ります（再起動後に有効）。"));
            changed |= config.Describe("unlockAuditMode", D(
                "解除审核模式：把 RemoteConfig.IsAuditMode 清为 false。设置页的天使模式开关、平台页签、联动角色等按 Steam 国服的非审核行为显示。",
                "解除審核模式：把 RemoteConfig.IsAuditMode 清為 false。設定頁的天使模式開關、平台頁籤、聯動角色等按 Steam 國服的非審核行為顯示。",
                "Lift audit mode: clears RemoteConfig.IsAuditMode to false, so the settings window's angel-mode switch and other audit-gated UI behave like the non-audit Steam CN client.",
                "審査モードを解除：RemoteConfig.IsAuditMode を false にします。設定画面の天使モード切替などがSteam中国版と同じ表示になります。"));
            changed |= config.Describe("unlockAngelMode", D(
                "解除天使模式限制：把 RemoteConfig.IsAngelMode 清为 false。该标记在热更代码里暂无消费点，主要是防止 AOT 侧用它强制和谐。",
                "解除天使模式限制：把 RemoteConfig.IsAngelMode 清為 false。該標記在熱更代碼裡暫無消費點，主要是防止 AOT 側用它強制和諧。",
                "Lift the angel-mode flag: clears RemoteConfig.IsAngelMode to false. The hot-update code currently has no consumer for it; this mainly guards against AOT-side forced harmony.",
                "天使モード制限を解除：RemoteConfig.IsAngelMode を false にします。ホットアップデート側では未使用ですが、AOT 側の強制ハーモニー防止用です。"));
            changed |= config.Describe("restoreSwitch", D(
                "恢复设置页开关：把「设置 → 功能」里被删减版 UI 包藏起来的天使模式开关行重新显示出来（从隐藏分组里摘出、恢复可见可点，并把审核页控制器压到非审核页）。关闭后只有上面两个标记清除生效。",
                "恢復設定頁開關：把「設定 → 功能」裡被精簡版 UI 包藏起來的天使模式開關列重新顯示（從隱藏分組移出、恢復可見可點，並把審核頁控制器壓到非審核頁）。關閉後只有上面兩個標記清除生效。",
                "Restore the settings switch: brings back the angel-mode row that the trimmed UI package hides in Settings → Features (detach it from the hidden group, restore visibility, and force the audit-page controller to the non-audit page). When off, only the two flag clears above apply.",
                "設定画面のスイッチを復元：「設定 → 機能」で削減版UIパッケージに隠された天使モード行を再表示します（非表示グループから外し、表示・操作可能に戻し、審査ページのコントローラを通常ページへ）。オフにすると上の2つのフラグ解除のみ有効です。"));
            changed |= config.Describe("switchTitle", D(
                "开关标题：仅在删减版包把标题文案抹空时使用，留空则按当前界面语言自动取（简中/繁中「天使模式」、英文 Angel Mode、日文 天使モード、韩文 엔젤 모드）。",
                "開關標題：僅在精簡版包把標題文案抹空時使用，留空則依目前介面語言自動取（簡中/繁中「天使模式」、英文 Angel Mode、日文 天使モード、韓文 엔젤 모드）。",
                "Switch label: used only when the trimmed package blanks the label; leave empty to pick by UI language (Chinese 天使模式, English Angel Mode, Japanese 天使モード, Korean 엔젤 모드).",
                "スイッチのラベル：削減版パッケージでラベルが空の場合のみ使用。空欄ならUI言語に合わせて自動選択します（中国語「天使模式」、英語 Angel Mode、日本語 天使モード、韓国語 엔젤 모드）。"));
            changed |= config.Describe("angelMode", D(
                "强制固定天使模式：keep=不干预（用游戏内设置里的开关自由切换，推荐）；sfw=强制开启天使模式（和谐遮挡）；unsfw=强制关闭（显示原始立绘）。注意 sfw/unsfw 会持续压住游戏内开关。",
                "強制固定天使模式：keep=不干預（用遊戲內設定裡的開關自由切換，推薦）；sfw=強制開啟天使模式（和諧遮擋）；unsfw=強制關閉（顯示原始立繪）。注意 sfw/unsfw 會持續壓住遊戲內開關。",
                "Force the angel mode: keep = do not intervene (toggle freely in the game's settings, recommended); sfw = force angel mode on (harmony covered); unsfw = force it off (original art shown). Note that sfw/unsfw keep overriding the in-game switch.",
                "天使モードを強制固定：keep=介入しない（ゲーム内設定で自由に切替、推奨）；sfw=天使モードを常時オン（ハーモニー表示）；unsfw=常時オフ（元の立ち絵を表示）。sfw/unsfw はゲーム内スイッチより優先されます。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
