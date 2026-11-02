using CesiumLoader.SDK.Configuration;

namespace AudioSampleMod
{
    /// <summary>
    /// config.json 里各字段的多语言说明。
    /// </summary>
    public static class AudioSampleConfigDocs
    {
        public static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;

            changed |= config.Describe("enabled", D(
                "总开关：关闭后禁用音频示例的所有功能。",
                "總開關：關閉後停用音訊範例的所有功能。",
                "Master switch: disables all features of the audio sample when off.",
                "全体スイッチ：オフにするとオーディオサンプルの全機能が無効になります。"));

            changed |= config.Describe("playKey", D(
                "播放测试音效的热键，默认 F8。",
                "播放測試音效的熱鍵，預設 F8。",
                "Hotkey to play the test sound effect (default: F8).",
                "テスト効果音を再生するホットキー（既定: F8）。"));

            changed |= config.Describe("volume", D(
                "测试音频的播放音量，范围 0.0 ~ 1.0，默认 0.8。",
                "測試音訊的播放音量，範圍 0.0 ~ 1.0，預設 0.8。",
                "Playback volume for test audio, range 0.0 to 1.0 (default: 0.8).",
                "テスト音声の再生音量（0.0〜1.0、既定 0.8）。"));

            changed |= config.Describe("replaceButtonClick", D(
                "是否拦截并替换游戏界面按钮点击声音为自定义高频滴声。",
                "是否攔截並替換遊戲介面按鈕點選聲音為自訂高頻提示音。",
                "Intercept and replace UI button click sounds with a custom high-frequency beep.",
                "UI ボタンのクリック音を傍受し、カスタムのビープ音に差し替えるかどうか。"));

            changed |= config.Describe("logSoundEvents", D(
                "是否在日志中打印游戏触发的每个声音事件 ID 与名称（方便 Mod 开发者探查游戏音效）。",
                "是否在記錄中輸出遊戲觸發的每個聲音事件 ID 與名稱（方便模組開發者探查遊戲音效）。",
                "Log all triggered in-game sound event IDs and names to the console (useful for modders).",
                "ゲームが再生した効果音イベントの ID と名前をログに出力します（Mod 開発用）。"));

            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
        {
            return ConfigDoc.Of(zhHans, zhHant, en, ja);
        }
    }
}
