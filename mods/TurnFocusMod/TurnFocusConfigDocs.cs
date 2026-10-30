using CesiumLoader.SDK.Configuration;

namespace TurnFocusMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class TurnFocusConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：关闭后不自动切换窗口，也不播放提示音。",
                "總開關：關閉後不自動切換視窗，也不播放提示音。",
                "Master switch: disables automatic window focus and sounds when off.",
                "全体スイッチ：オフにするとウィンドウの自動切り替えと通知音を行いません。"));
            changed |= config.Describe("cooldownSeconds", D(
                "两次自动切窗之间的最短间隔（秒），范围 0–60，默认 1。",
                "兩次自動切窗之間的最短間隔（秒），範圍 0–60，預設 1。",
                "Minimum interval between automatic window focus changes in seconds (0–60, default 1).",
                "自動でウィンドウを切り替える最短間隔（秒、0～60、既定 1）。"));
            changed |= config.Describe("focusOnRoomChat", D(
                "开房后有人发准备消息时切到游戏窗口。",
                "開房後有人傳送準備訊息時切到遊戲視窗。",
                "Focus the game window when someone sends a ready message in your room.",
                "ルームで準備メッセージが届いたときにゲームウィンドウへ切り替えます。"));
            changed |= config.Describe("focusOnRoomInvite", D(
                "收到房间邀请（右上角出现邀请按钮）时切到游戏窗口。",
                "收到房間邀請（右上角出現邀請按鈕）時切到遊戲視窗。",
                "Focus the game window when you receive a room invitation (the invite button in the top-right corner).",
                "ルーム招待を受け取ったとき（右上に招待ボタンが表示されたとき）にゲームウィンドウへ切り替えます。"));
            changed |= config.Describe("focusOnMatchSuccess", D(
                "匹配成功时切到游戏窗口。",
                "配對成功時切到遊戲視窗。",
                "Focus the game window when matchmaking succeeds.",
                "マッチング成功時にゲームウィンドウへ切り替えます。"));
            changed |= config.Describe("soundEnabled", D(
                "播放提示音。",
                "播放提示音。",
                "Play a notification sound.",
                "通知音を再生します。"));
            changed |= config.Describe("soundFile", D(
                "提示音文件路径；留空时使用默认提示音。",
                "提示音檔案路徑；留空時使用預設提示音。",
                "Notification sound file path; leave empty to use the default sound.",
                "通知音のファイルパス。空欄なら既定の音を使います。"));
            changed |= config.Describe("alwaysPlaySound", D(
                "窗口已经在前台时也播放提示音；默认只在需要切窗时播放。",
                "視窗已經在前景時也播放提示音；預設只在需要切窗時播放。",
                "Play the sound even when the window is already focused; by default it only plays when focus changes.",
                "すでに最前面にある場合も通知音を鳴らします（既定は切り替えが必要なときのみ）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
