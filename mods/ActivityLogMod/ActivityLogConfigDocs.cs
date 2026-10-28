using CesiumLoader.SDK.Configuration;

namespace ActivityLogMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class ActivityLogConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("Enabled", D(
                "总开关：是否记录实时行为日志。",
                "總開關：是否記錄即時行為日誌。",
                "Master switch for activity logging.",
                "リアルタイム行動ログを記録するかの全体スイッチ。"));
            changed |= config.Describe("LogUi", D(
                "记录界面操作（打开/关闭窗口、点击按钮等）。",
                "記錄介面操作（開啟/關閉視窗、點擊按鈕等）。",
                "Log UI actions such as opening windows and clicking buttons.",
                "ウィンドウの開閉やボタン操作などの UI 操作を記録します。"));
            changed |= config.Describe("LogBattle", D(
                "记录战斗过程（出牌、攻击、防御、伤害等）。",
                "記錄戰鬥過程（出牌、攻擊、防禦、傷害等）。",
                "Log battle actions such as playing cards, attacks, defense, and damage.",
                "カード使用・攻撃・防御・ダメージなどの戦闘内容を記録します。"));
            changed |= config.Describe("LogEvents", D(
                "记录游戏事件与动作广播。",
                "記錄遊戲事件與動作廣播。",
                "Log game events and action broadcasts.",
                "ゲームイベントとアクション放送を記録します。"));
            changed |= config.Describe("LogTurn", D(
                "记录回合开始、结束与当前玩家变化。",
                "記錄回合開始、結束與目前玩家變化。",
                "Log turn start/end and current-player changes.",
                "ターン開始・終了と現在のプレイヤー変化を記録します。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
