using CesiumLoader.SDK.Configuration;

namespace AutoThanksMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class AutoThanksConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：关闭后不发送感谢，也不自动点赞。",
                "總開關：關閉後不傳送感謝，也不自動按讚。",
                "Master switch: disables thank-you messages and automatic likes.",
                "全体スイッチ：オフにすると感謝メッセージと自動いいねを行いません。"));
            changed |= config.Describe("thankForHealing", D(
                "队友治疗你时发送感谢。",
                "隊友治療你時傳送感謝。",
                "Send thanks when a teammate heals you.",
                "味方が回復してくれたときに感謝を送ります。"));
            changed |= config.Describe("thankForCards", D(
                "队友给你牌时发送感谢。",
                "隊友給你牌時傳送感謝。",
                "Send thanks when a teammate gives you cards.",
                "味方がカードを渡してくれたときに感謝を送ります。"));
            changed |= config.Describe("thankForTransfers", D(
                "队友转星币给你时发送感谢。",
                "隊友轉星幣給你時傳送感謝。",
                "Send thanks when a teammate transfers Star Coins to you.",
                "味方がスターコインを送ってくれたときに感謝を送ります。"));
            changed |= config.Describe("cooldownSeconds", D(
                "两次感谢之间的最短间隔（秒），范围 0–30，默认 2。",
                "兩次感謝之間的最短間隔（秒），範圍 0–30，預設 2。",
                "Minimum interval between thank-you messages in seconds (0–30, default 2).",
                "感謝メッセージの最短間隔（秒、0～30、既定 2）。"));
            changed |= config.Describe("thankDelayMilliseconds", D(
                "收到帮助后等待多少毫秒再发送感谢，范围 0–3000，默认 1000。",
                "收到幫助後等待多少毫秒再傳送感謝，範圍 0–3000，預設 1000。",
                "Delay before sending thanks after help is received, in milliseconds (0–3000, default 1000).",
                "助けを受けてから感謝を送るまでの待ち時間（ミリ秒、0～3000、既定 1000）。"));
            changed |= config.Describe("autoLikeOnGameEnd", D(
                "游戏结束后自动点赞其他真人玩家。",
                "遊戲結束後自動按讚其他真人玩家。",
                "Automatically like other human players after the game ends.",
                "ゲーム終了後に他の人間プレイヤーへ自動でいいねします。"));
            changed |= config.Describe("likeIntervalMilliseconds", D(
                "自动点赞的间隔（毫秒），默认 500。",
                "自動按讚的間隔（毫秒），預設 500。",
                "Interval between automatic likes in milliseconds (default 500).",
                "自動いいねの間隔（ミリ秒、既定 500）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
