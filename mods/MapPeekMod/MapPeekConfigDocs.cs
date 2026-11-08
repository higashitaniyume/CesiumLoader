using CesiumLoader.SDK.Configuration;

namespace MapPeekMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class MapPeekConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：选人阶段是否在左上角显示本局地图。",
                "總開關：選人階段是否在左上角顯示本局地圖。",
                "Master switch: show the current map during hero selection (top-left).",
                "全体スイッチ：キャラクター選択中に現在のマップを左上に表示するかどうか。"));
            changed |= config.Describe("fontSize", D(
                "地图浮窗文本字号，范围 12–36，默认 18。",
                "地圖浮窗文字字級，範圍 12–36，預設 18。",
                "Map overlay font size (12–36, default 18).",
                "マップオーバーレイの文字サイズ（12～36、既定 18）。"));
            changed |= config.Describe("panelWidth", D(
                "地图浮窗宽度（像素），范围 200–600，默认 340。",
                "地圖浮窗寬度（像素），範圍 200–600，預設 340。",
                "Map overlay panel width in pixels (200–600, default 340).",
                "マップパネルの幅（ピクセル、200～600、既定 340）。"));
            changed |= config.Describe("offsetX", D(
                "地图浮窗距离屏幕左边缘的水平偏移量（像素），默认 20。",
                "地圖浮窗距離螢幕左邊緣的水平偏移量（像素），預設 20。",
                "Map overlay distance from the left screen edge in pixels (default 20).",
                "マップパネルの画面左端からの水平オフセット（ピクセル、既定 20）。"));
            changed |= config.Describe("offsetY", D(
                "地图浮窗距离屏幕顶部的垂直偏移量（像素），默认 70。",
                "地圖浮窗距離螢幕頂部的垂直偏移量（像素），預設 70。",
                "Map overlay distance from the top screen edge in pixels (default 70).",
                "マップパネルの画面上端からの垂直オフセット（ピクセル、既定 70）。"));
            changed |= config.Describe("showMapId", D(
                "是否显示地图 ID（如地图 82013）。",
                "是否顯示地圖 ID（如地圖 82013）。",
                "Whether to show the map ID.",
                "マップID（例 82013）を表示するかどうか。"));
            changed |= config.Describe("showMode", D(
                "是否显示玩法模式（如 PVE / PVP）。",
                "是否顯示玩法模式（如 PVE / PVP）。",
                "Whether to show the game mode (e.g. PVE / PVP).",
                "ゲームモード（PVE / PVP など）を表示するかどうか。"));
            changed |= config.Describe("showDifficulty", D(
                "是否显示难度（PVE 显示难度名，PVP 显示难度编号）。",
                "是否顯示難度（PVE 顯示難度名，PVP 顯示難度編號）。",
                "Whether to show difficulty (named for PVE, numeric for PVP).",
                "難易度（PVE は名称、PVP は番号）を表示するかどうか。"));
            changed |= config.Describe("showPlayerCount", D(
                "是否显示对局人数。",
                "是否顯示對局人數。",
                "Whether to show player count.",
                "対局人数を表示するかどうか。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
