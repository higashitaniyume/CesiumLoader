using CesiumLoader.SDK.Configuration;

namespace QuickChatPlusMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class QuickChatPlusConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：关闭后战斗内快捷短语恢复游戏原版列表。",
                "總開關：關閉後戰鬥內快捷短語恢復遊戲原版列表。",
                "Master switch: restoring the game's original in-battle phrase list when off.",
                "全体スイッチ：オフにすると戦闘中のクイックフレーズがゲーム本来の一覧に戻ります。"));
            changed |= config.Describe("includeRoomChats", D(
                "把房间等待阶段的快捷短语（准备、谢谢等）也加入战斗内列表。",
                "把房間等待階段的快捷短語（準備、謝謝等）也加入戰鬥內列表。",
                "Also add room-waiting phrases (ready, thanks, and so on) to the in-battle list.",
                "ルーム待機中のクイックフレーズ（準備、ありがとう等）も戦闘中の一覧に追加します。"));
            changed |= config.Describe("includeResultChats", D(
                "把结算阶段的快捷短语加入战斗内列表。",
                "把結算階段的快捷短語加入戰鬥內列表。",
                "Also add settlement phrases to the in-battle list.",
                "リザルト画面のクイックフレーズも戦闘中の一覧に追加します。"));
            changed |= config.Describe("includePvpModes", D(
                "是否也给 PVP 等非 PVE 模式补全短语；默认只改 PVE 模式。",
                "是否也給 PVP 等非 PVE 模式補全短語；預設只改 PVE 模式。",
                "Also fill the list in PVP and other non-PVE modes; by default only PVE modes are changed.",
                "PVP など PVE 以外のモードにも一覧を補完するか。既定では PVE モードのみ変更します。"));
            changed |= config.Describe("maxVisibleItems", D(
                "列表最多同时显示几条，超出后可用滚轮或拖动滚动；0 表示按屏幕可用高度尽量多；默认 16，范围 0–60。",
                "列表最多同時顯示幾條，超出後可用滾輪或拖曳捲動；0 表示依螢幕可用高度盡量多；預設 16，範圍 0–60。",
                "Maximum rows visible at once; scroll with the mouse wheel or drag for the rest. 0 fits as many as the screen allows. Default 16, range 0–60.",
                "一覧に同時表示する最大行数。超えた分はホイールまたはドラッグでスクロールします。0 は画面に収まるだけ表示。既定 16、範囲 0～60。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
