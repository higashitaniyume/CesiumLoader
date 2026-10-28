using CesiumLoader.SDK.Configuration;

namespace SpeedHackMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class SpeedHackConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("speed", D(
                "记住的当前倍速，启动时恢复；范围 1.0–10.0。",
                "記住的目前倍速，啟動時還原；範圍 1.0–10.0。",
                "Remembered current speed restored on startup (1.0–10.0).",
                "記憶した現在の倍速。起動時に復元されます（1.0～10.0）。"));
            changed |= config.Describe("speedStep", D(
                "每次加速/减速的幅度，默认 0.5。",
                "每次加速/減速的幅度，預設 0.5。",
                "Amount added or removed per speed change (default 0.5).",
                "1 回の加速/減速量（既定 0.5）。"));
            changed |= config.Describe("minSpeed", D(
                "允许的最低倍速，默认 1.0；低于 1.0 会被拒绝。",
                "允許的最低倍速，預設 1.0；低於 1.0 會被拒絕。",
                "Minimum allowed speed (default 1.0); values below 1.0 are rejected.",
                "許可する最低倍速（既定 1.0）。1.0 未満は拒否されます。"));
            changed |= config.Describe("maxSpeed", D(
                "允许的最高倍速，默认 10.0。",
                "允許的最高倍速，預設 10.0。",
                "Maximum allowed speed (default 10.0).",
                "許可する最高倍速（既定 10.0）。"));
            changed |= config.Describe("toggleKey", D(
                "在正常速度与加速之间切换的按键，默认 Delete。",
                "在正常速度與加速之間切換的按鍵，預設 Delete。",
                "Key that toggles between normal and accelerated speed (default Delete).",
                "通常速度と加速を切り替えるキー（既定 Delete）。"));
            changed |= config.Describe("speedUpKey", D(
                "加速按键，默认 Equals（=）。",
                "加速按鍵，預設 Equals（=）。",
                "Speed-up key (default Equals, the = key).",
                "加速キー（既定 Equals、= キー）。"));
            changed |= config.Describe("speedDownKey", D(
                "减速按键，默认 Minus（-）。",
                "減速按鍵，預設 Minus（-）。",
                "Slow-down key (default Minus, the - key).",
                "減速キー（既定 Minus、- キー）。"));
            changed |= config.Describe("useNumpadKeys", D(
                "小键盘 + / - 也用于调速。",
                "小鍵盤 + / - 也用於調速。",
                "Also use the numpad + / - keys for speed control.",
                "テンキーの + / - でも速度を変更します。"));
            changed |= config.Describe("notify", D(
                "切换倍速时显示屏幕提示。",
                "切換倍速時顯示螢幕提示。",
                "Show an on-screen notice when changing speed.",
                "倍速変更時に画面へ通知を表示します。"));
            changed |= config.Describe("rememberSpeed", D(
                "退出或切换时把当前倍速写回 config.json。",
                "離開或切換時把目前倍速寫回 config.json。",
                "Write the current speed back to config.json when it changes.",
                "終了時や変更時に現在の倍速を config.json へ保存します。"));
            changed |= config.Describe("toggleRestoresSpeed", D(
                "toggleKey 再按一次时恢复到加速前的速度。",
                "toggleKey 再按一次時還原到加速前的速度。",
                "Pressing toggleKey again restores the speed from before acceleration.",
                "toggleKey をもう一度押すと加速前の速度へ戻します。"));
            changed |= config.Describe("repeatInterval", D(
                "按住加速/减速键的连发间隔（秒），0 表示只触发一次，默认 0.15。",
                "按住加速/減速鍵的連發間隔（秒），0 表示只觸發一次，預設 0.15。",
                "Repeat interval while holding a speed key in seconds; 0 fires once (default 0.15).",
                "速度キーを押し続けたときの連続間隔（秒）。0 で 1 回のみ（既定 0.15）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
