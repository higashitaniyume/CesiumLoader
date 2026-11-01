using CesiumLoader.SDK.Configuration;

namespace PveWaveForecastMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class PveWaveForecastConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "启动时是否显示任务波次预告，默认关闭；游戏内按 O 打开/关闭。",
                "啟動時是否顯示任務波次預告，預設關閉；遊戲內按 O 開啟/關閉。",
                "Whether the forecast starts visible. Default off; press O in game to toggle.",
                "起動時に予告を表示するか。既定はオフ。ゲーム内で O を押して切り替えます。"));
            changed |= config.Describe("toggleKey", D(
                "游戏内打开/关闭预告的按键，默认 O。",
                "遊戲內開啟/關閉預告的按鍵，預設 O。",
                "In-game toggle key, default O.",
                "ゲーム内で予告を切り替えるキー（既定 O）。"));
            changed |= config.Describe("fontSize", D(
                "地图标记文字字号，范围 14–48，默认 22。",
                "地圖標記文字字級，範圍 14–48，預設 22。",
                "Map marker font size (14–48, default 22).",
                "マップマーカーの文字サイズ（14～48、既定 22）。"));
            changed |= config.Describe("heightOffset", D(
                "标记相对地块的高度偏移（米），范围 0–10，默认 1.6。",
                "標記相對地塊的高度偏移（公尺），範圍 0–10，預設 1.6。",
                "Marker height offset above the land in meters (0–10, default 1.6).",
                "地塊からのマーカー高さオフセット（メートル、0～10、既定 1.6）。"));
            changed |= config.Describe("labelLift", D(
                "标记在屏幕上的额外上移像素，避免与岔路地块标记重叠；范围 0–160，默认 36。",
                "標記在螢幕上的額外上移像素，避免與岔路地塊標記重疊；範圍 0–160，預設 36。",
                "Extra screen-space upward offset in pixels to clear fork markers (0–160, default 36).",
                "分岐マーカーと重ならないよう画面座標で追加で上へずらすピクセル数（0～160、既定 36）。"));
            changed |= config.Describe("showSchedulePanel", D(
                "是否在左上角显示本局全部任务波次列表。",
                "是否在左上角顯示本局全部任務波次列表。",
                "Show the full task-wave list in the top-left corner.",
                "画面左上に全タスクウェーブ一覧を表示します。"));
            changed |= config.Describe("showOnlyNextWave", D(
                "只在地图上标记“当前/下一波”，关闭后标记列表内所有波次。",
                "只在地圖上標記「當前/下一波」，關閉後標記列表內所有波次。",
                "Only mark the current/next wave on the map; turn off to mark every listed wave.",
                "マップ上は現在／次ウェーブのみ表示。オフで一覧内の全ウェーブを表示します。"));
            changed |= config.Describe("showRandomLandHints", D(
                "当配置没有给出精确地块时，是否标出所有怪物地块作为位置提示。",
                "當設定沒有給出精確地塊時，是否標出所有怪物地塊作為位置提示。",
                "When the config has no exact lands, mark every Monster land as a location hint.",
                "設定に正確な地塊がない場合、全モンスター地塊を位置ヒントとして表示します。"));
            changed |= config.Describe("maxWaves", D(
                "左上角列表最多显示多少个波次，范围 1–20，默认 8。",
                "左上角列表最多顯示多少個波次，範圍 1–20，預設 8。",
                "Maximum waves shown in the top-left list (1–20, default 8).",
                "左上リストに表示するウェーブ数の上限（1～20、既定 8）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
