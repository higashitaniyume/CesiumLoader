using CesiumLoader.SDK.Configuration;

namespace ShopRelicPeekMod
{
    internal static class ShopRelicPeekConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;

            changed |= config.Describe("enabled", D(
                "总开关：在游戏屏幕右上侧展示进店卡牌与选筹码候选历史看板。",
                "總開關：在遊戲螢幕右上側展示進店卡牌與選籌碼候選歷史看板。",
                "Master switch: display visited shop cards and relic selection candidates overlay.",
                "全体スイッチ：ゲーム右上にショップのカードとチップの候補履歴看板を表示します。"));

            changed |= config.Describe("fontSize", D(
                "看板文本字号，范围 12–36，默认 18。",
                "看板文字字級，範圍 12–36，預設 18。",
                "Overlay font size (12–36, default 18).",
                "文字サイズ（12～36、既定 18）。"));

            changed |= config.Describe("clearKey", D(
                "一键清空看板内容的快捷键，默认 T。",
                "一鍵清空看板內容的快捷鍵，預設 T。",
                "Hotkey to instantly clear overlay content (default T).",
                "看板の内容を一括クリアするショートカットキー（既定 T）。"));

            changed |= config.Describe("showFloorTag", D(
                "玩家昵称前是否显示一楼至四楼的座位标签（如【一楼】）。",
                "玩家暱稱前是否顯示一樓至四樓的座位標籤（如【一樓】）。",
                "Whether to prefix player names with seat floor tags (e.g. 1st Floor).",
                "プレイヤー名の前に座席階層タグ（1階～4階）を表示するかどうか。"));

            // 商店看板设置
            changed |= config.Describe("showShopCards", D(
                "是否开启商店独立看板（展示玩家进入商店遇到的卡牌）。",
                "是否開啟商店獨立看板（展示玩家進入商店遇到的卡牌）。",
                "Whether to enable the independent shop overlay.",
                "ショップ独立看板を有効にするかどうか（遭遇したカードを表示）。"));

            changed |= config.Describe("shopPanelWidth", D(
                "商店看板面板宽度（像素），范围 200–600，默认 340。",
                "商店看板面板寬度（像素），範圍 200–600，預設 340。",
                "Shop panel width in pixels (200–600, default 340).",
                "ショップ看板の幅（ピクセル、200～600、既定 340）。"));

            changed |= config.Describe("shopOffsetX", D(
                "商店看板距离屏幕右边缘的水平偏移量（像素），默认 20。",
                "商店看板距離螢幕右邊緣的水平偏移量（像素），預設 20。",
                "Shop panel distance from the right screen edge in pixels (default 20).",
                "ショップ看板の画面右端からの水平オフセット（ピクセル、既定 20）。"));

            changed |= config.Describe("shopOffsetY", D(
                "商店看板距离屏幕顶部的垂直偏移量（像素），默认 70。",
                "商店看板距離螢幕頂部的垂直偏移量（像素），預設 70。",
                "Shop panel distance from the top screen edge in pixels (default 70).",
                "ショップ看板の画面上端からの垂直オフセット（ピクセル、既定 70）。"));

            changed |= config.Describe("shopDisplayDurationSeconds", D(
                "离开商店后货架卡牌保持显示的秒数，默认 20 秒。",
                "離開商店後貨架卡牌保持顯示的秒數，預設 20 秒。",
                "Duration in seconds to keep showing shop cards after leaving the shop (default 20).",
                "ショップ退出後もカードを表示し続ける秒数（既定 20 秒）。"));

            // 筹码看板设置
            changed |= config.Describe("showRelicCandidates", D(
                "是否开启筹码独立看板（展示候选筹码、刷新历史及最终选定）。",
                "是否開啟籌碼獨立看板（展示候選籌碼、刷新歷史及最終選定）。",
                "Whether to enable the independent relic selection overlay.",
                "チップ独立看板を有効にするかどうか（候補、リロール履歴、選択結果を表示）。"));

            changed |= config.Describe("relicPanelWidth", D(
                "筹码看板面板宽度（像素），范围 200–600，默认 340。",
                "籌碼看板面板寬度（像素），範圍 200–600，預設 340。",
                "Relic panel width in pixels (200–600, default 340).",
                "チップ看板の幅（ピクセル、200～600、既定 340）。"));

            changed |= config.Describe("relicOffsetX", D(
                "筹码看板距离屏幕右边缘的水平偏移量（像素），默认 20。",
                "籌碼看板距離螢幕右邊緣的水平偏移量（像素），預設 20。",
                "Relic panel distance from the right screen edge in pixels (default 20).",
                "チップ看板の画面右端からの水平オフセット（ピクセル、既定 20）。"));

            changed |= config.Describe("relicOffsetY", D(
                "筹码看板距离屏幕顶部的垂直偏移量（像素），默认 320（位于商店下方，且自动避让商店不遮挡）。",
                "籌碼看板距離螢幕頂部的垂直偏移量（像素），預設 320（位於商店下方，且自動避讓商店不遮擋）。",
                "Relic panel distance from the top screen edge in pixels (default 320, auto-avoids shop).",
                "チップ看板の画面上端からの垂直オフセット（ピクセル、既定 320、ショップを自動回避）。"));

            changed |= config.Describe("relicDisplayDurationSeconds", D(
                "选定筹码后候选历史与选定结果保持显示的秒数，默认 20 秒（防止瞬间消失）。",
                "選定籌碼後候選歷史與選定結果保持顯示的秒數，預設 20 秒（防止瞬間消失）。",
                "Duration in seconds to keep showing relic history and chosen result after selection (default 20).",
                "チップ決定後も履歴と結果を表示し続ける秒数（既定 20 秒、即時消失を防止）。"));

            changed |= config.Describe("colorRelicByQuality", D(
                "候选筹码名称是否按品质着色（普通蓝、稀有紫、传说橙）。",
                "候選籌碼名稱是否按品質著色（普通藍、稀有紫、傳說橙）。",
                "Whether to color relic names by quality (common blue, rare purple, legendary orange).",
                "チップの名前を品質別（ノーマル青、レア紫、レジェンド橙）に着色するかどうか。"));

            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
