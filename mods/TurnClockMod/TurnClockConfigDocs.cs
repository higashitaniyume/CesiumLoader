using CesiumLoader.SDK.Configuration;

namespace TurnClockMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class TurnClockConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：显示回合计时。",
                "總開關：顯示回合計時。",
                "Master switch for the turn timer.",
                "ターンタイマー表示の全体スイッチ。"));
            changed |= config.Describe("otherPlayers", D(
                "也为其他玩家显示回合计时。",
                "也為其他玩家顯示回合計時。",
                "Also show turn timers for other players.",
                "他のプレイヤーのターンタイマーも表示します。"));
            changed |= config.Describe("dryRun", D(
                "只探测计时字段并写日志，不修改界面。",
                "只探測計時欄位並寫日誌，不修改介面。",
                "Only probe timer fields and log them without changing the UI.",
                "タイマー項目を検出してログに出すだけで、画面は変更しません。"));
            changed |= config.Describe("logWindow", D(
                "把计时窗口的探测结果写入日志。",
                "把計時視窗的探測結果寫入日誌。",
                "Write timer-window probe results to the log.",
                "タイマーウィンドウの検出結果をログへ出力します。"));
            changed |= config.Describe("speedRefreshSeconds", D(
                "读取速度字段的刷新间隔（秒），范围 0.05–5，默认 0.2。",
                "讀取速度欄位的重新整理間隔（秒），範圍 0.05–5，預設 0.2。",
                "Refresh interval for reading speed fields in seconds (0.05–5, default 0.2).",
                "速度項目を読み取る更新間隔（秒、0.05～5、既定 0.2）。"));
            changed |= config.Describe("elapsedMember", D(
                "手动指定已用时间字段名；留空时自动探测。",
                "手動指定已用時間欄位名稱；留空時自動探測。",
                "Manually specify the elapsed-time field name; leave empty to auto-detect.",
                "経過時間の項目名を手動指定します。空欄なら自動検出。"));
            changed |= config.Describe("startMember", D(
                "手动指定计时开始字段名；留空时自动探测。",
                "手動指定計時開始欄位名稱；留空時自動探測。",
                "Manually specify the timer start field name; leave empty to auto-detect.",
                "タイマー開始項目名を手動指定します。空欄なら自動検出。"));
            changed |= config.Describe("clockMember", D(
                "手动指定时钟字段名；留空时自动探测。",
                "手動指定時鐘欄位名稱；留空時自動探測。",
                "Manually specify the clock field name; leave empty to auto-detect.",
                "時計項目名を手動指定します。空欄なら自動検出。"));
            changed |= config.Describe("durationMember", D(
                "手动指定回合计时长度字段名，默认 duration。",
                "手動指定回合計時長度欄位名稱，預設 duration。",
                "Manually specify the turn duration field name (default: duration).",
                "ターン制限時間の項目名を手動指定します（既定: duration）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
