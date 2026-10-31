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
                "总开关：关闭后不读取也不修改任何计时器。",
                "總開關：關閉後不讀取也不修改任何計時器。",
                "Master switch: when off, no timer is read or modified.",
                "全体スイッチ：オフにするとタイマーを読み取りも変更もしません。"));
            changed |= config.Describe("otherPlayers", D(
                "其他玩家回合的倒计时显示也按真实时间走。",
                "其他玩家回合的倒數計時顯示也按真實時間走。",
                "Also show other players' turn countdown in real time.",
                "他のプレイヤーのターン残り時間も実時間で表示します。"));
            changed |= config.Describe("heroSelect", D(
                "选人界面的倒计时按真实时间走：选英雄与选皮肤到点不再提前自动确认。",
                "選人介面的倒數計時按真實時間走：選英雄與選造型到點不再提前自動確認。",
                "Keep the hero-select countdown in real time: hero and skin picks are no longer auto-confirmed early.",
                "ヒーロー選択画面のカウントダウンを実時間で進めます（ヒーロー／スキンの自動確定が早まりません）。"));
            changed |= config.Describe("settlement", D(
                "结算界面的自动翻页倒计时按真实时间走。",
                "結算介面的自動翻頁倒數計時按真實時間走。",
                "Keep the settlement screen's auto-advance countdown in real time.",
                "リザルト画面の自動送りカウントダウンを実時間で進めます。"));
            changed |= config.Describe("roomWait", D(
                "房间等待倒计时按真实时间走：等待超时不再提前退出房间，房主也不再提前转让。",
                "房間等待倒數計時按真實時間走：等待逾時不再提前退出房間，房主也不再提前轉讓。",
                "Keep room-wait timers in real time: the room no longer closes early and host handover is not rushed.",
                "ルーム待機のカウントダウンを実時間で進めます（早期のルーム解散やホスト移譲を防ぎます）。"));
            changed |= config.Describe("storyAuto", D(
                "剧情自动播放的翻页倒计时按真实时间走。",
                "劇情自動播放的翻頁倒數計時按真實時間走。",
                "Keep the story auto-play page countdown in real time.",
                "ストーリー自動再生のページ送りカウントダウンを実時間で進めます。"));
            changed |= config.Describe("dryRun", D(
                "只跟踪、只记录，不改计时器（排查用）。",
                "只追蹤、只記錄，不改計時器（排查用）。",
                "Only track and log timers without modifying them (for troubleshooting).",
                "タイマーを追跡・記録するだけで変更しません（調査用）。"));
            changed |= config.Describe("logWindow", D(
                "把每个倒计时窗口的补偿结果写入日志（默认开启）。",
                "把每個倒數計時視窗的補償結果寫入日誌（預設開啟）。",
                "Write per-window compensation results to the log (default on).",
                "各カウントダウンの補正結果をログへ出力します（既定オン）。"));
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
                "手动指定倒计时长度字段名，默认 duration。",
                "手動指定倒數計時長度欄位名稱，預設 duration。",
                "Manually specify the countdown duration field name (default: duration).",
                "カウントダウンの制限時間項目名を手動指定します（既定: duration）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
