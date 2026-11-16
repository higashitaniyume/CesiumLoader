using CesiumLoader.SDK.Configuration;

namespace UpdateNoticeMod
{
    /// <summary>config.json 里各字段的多语言说明（Toys 配置弹窗按界面语言展示，缺失回退简中）。</summary>
    internal static class UpdateNoticeConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;

            var changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：关闭后本模组完全不介入，不读检查结果也不弹面板（改了立即生效）。",
                "總開關：關閉後本模組完全不介入，不讀檢查結果也不彈面板（改了立即生效）。",
                "Master switch: when off the mod does nothing — it neither reads the check result nor shows the panel (takes effect immediately).",
                "全体スイッチ：オフにすると本Modは一切介入せず、チェック結果の読み取りもパネル表示も行いません（即時反映）。"));

            changed |= config.Describe("autoShow", D(
                "启动后自动弹出：发现新版本时，进游戏几秒后自动弹一次面板。关闭后只在按快捷键时显示。",
                "啟動後自動彈出：發現新版本時，進遊戲幾秒後自動彈一次面板。關閉後只在按快捷鍵時顯示。",
                "Auto prompt: when a new version is found, show the panel once a few seconds after launch. When off, the panel only appears on the hotkey.",
                "起動後に自動表示：新しいバージョンが見つかったとき、起動数秒後にパネルを一度表示します。オフにするとホットキーでのみ表示します。"));

            changed |= config.Describe("showReleaseNotes", D(
                "显示更新说明：面板里显示发布清单里的 releaseNotes（一般留空）。关闭后只显示版本号和发布页地址。",
                "顯示更新說明：面板裡顯示發佈清單裡的 releaseNotes（通常留空）。關閉後只顯示版本號和發佈頁地址。",
                "Show release notes: display releaseNotes from the release manifest (usually empty). When off, only the version numbers and the release page are shown.",
                "リリースノートを表示：リリース情報の releaseNotes（通常は空）を表示します。オフにするとバージョンとリリースページのみ表示します。"));

            changed |= config.Describe("toggleKey", D(
                "打开/关闭提示面板的快捷键，默认 F8；填 None 关掉快捷键。名字以 Key 结尾，Toys 配置页会显示「按下按键」捕获控件。",
                "開啟/關閉提示面板的快捷鍵，預設 F8；填 None 關掉快捷鍵。名字以 Key 結尾，Toys 設定頁會顯示「按下按鍵」擷取控件。",
                "Hotkey that toggles the notice panel, default F8; set None to disable. The name ends with Key, so the Toys config page shows a press-a-key capture control.",
                "パネル表示を切り替えるホットキー（既定 F8、None で無効）。名前が Key で終わるため、Toys の設定画面ではキー入力のキャプチャになります。"));

            changed |= config.Describe("ignoredVersion", D(
                "被「忽略此版本」写进来的版本号。该版本不再自动弹面板；清空或换成别的版本号即可恢复提示。",
                "被「忽略此版本」寫進來的版本號。該版本不再自動彈面板；清空或換成別的版本號即可恢復提示。",
                "Version number recorded by the panel's \"ignore this version\" action. That version never auto-prompts again; clear it (or change it) to restore prompting.",
                "パネルの「このバージョンを無視」で記録されるバージョン。このバージョンは自動表示されなくなります。空にするか別の値にすると再び表示されます。"));

            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
        {
            return ConfigDoc.Of(zhHans, zhHant, en, ja);
        }
    }
}
