using CesiumLoader.SDK.Configuration;

namespace ChatDurationMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class ChatDurationConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：关闭后消息与表情恢复游戏原版的停留时长。",
                "總開關：關閉後訊息與表情恢復遊戲原版的停留時長。",
                "Master switch: restoring the game's original message and expression duration when off.",
                "全体スイッチ：オフにするとメッセージとスタンプの表示時間がゲーム本来の値に戻ります。"));
            changed |= config.Describe("durationSeconds", D(
                "战斗消息列表中的消息和表情在屏幕上停留的秒数，按真实时间计算，不受变速影响；默认 15，范围 1–300。",
                "戰鬥訊息列表中的訊息和表情在畫面上停留的秒數，依真實時間計算，不受變速影響；預設 15，範圍 1–300。",
                "Seconds that messages and expressions in the battle message list stay on screen, measured in real time and unaffected by speed changes. Default 15, range 1–300.",
                "戦闘メッセージ一覧のメッセージとスタンプを画面に表示する秒数（実時間、速度変更の影響なし）。既定 15、範囲 1～300。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
