using CesiumLoader.SDK.Configuration;

namespace PvePanelMod
{
    /// <summary>config.json 里各字段的多语言说明（照 CesiumLoader 内置 mod 的规矩：简中、繁中、英文、日文）。</summary>
    internal static class PanelConfigDocs
    {
        public const string Enabled = "Enabled";
        public const string HandService = "HandService";
        public const string HandServiceUrl = "HandServiceUrl";
        public const string DefaultServiceUrl = "https://aspac.hiynet.com/";
        public const string LeftToggleKey = "LeftToggleKey", HandToggleKey = "HandToggleKey", RightToggleKey = "RightToggleKey";
        public const string RightPanelHidden = "RightPanelHidden";
        public const string LeftPanelCollapsed = "LeftPanelCollapsed";
        public const string AutoCollapse = "AutoCollapse";
        public const string UiScale = "UiScale";
        public const string GridMap = "GridMap";
        public const string DefaultLeftKey = "F3", DefaultHandKey = "F4", DefaultRightKey = "F5";

        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe(Enabled, D(
                "是否显示 PVE 面板。默认关；本 mod 被加载器加载后，改这里立即生效（不用重启游戏），面板只在 PVE 对局里出现。",
                "是否顯示 PVE 面板。預設關；本 mod 被載入器載入後，改這裡立即生效（不用重啟遊戲），面板只在 PVE 對局裡出現。",
                "Whether to show the PVE panel. Off by default; once the loader has loaded this mod, changing it takes effect immediately (no restart), and the panel only appears in PVE matches.",
                "PVE パネルを表示するかどうか。既定はオフ。ローダーがこの mod を読み込んだ後は、変更が即座に反映されます（再起動不要）。PVE の対局でのみ表示されます。"));
            changed |= config.Describe(UiScale, D(
                "面板整体缩放（左栏、右栏、战斗面板、手牌抽屉、悬停小字一起变），范围 0.8–1.5，默认 1.0。游戏里点左栏底部的「－」「＋」按钮也能调，会自动记下。",
                "面板整體縮放（左欄、右欄、戰鬥面板、手牌抽屜、懸停小字一起變），範圍 0.8–1.5，預設 1.0。遊戲裡點左欄底部的「－」「＋」按鈕也能調，會自動記下。",
                "Overall panel scale (left bar, right bar, battle panel, hand drawer and tooltips together), 0.8–1.5, default 1.0. The - / + buttons at the bottom of the left bar adjust it in game and are saved automatically.",
                "パネル全体の拡大率（左バー・右バー・戦闘パネル・手札ドロワー・ツールチップを一括）。範囲 0.8～1.5、既定 1.0。ゲーム内では左バー下部の「－」「＋」でも調整でき、自動で保存されます。"));
            changed |= config.Describe(LeftToggleKey, D(
                "收起 / 展开左侧信息栏的快捷键，默认 F3；填 None 不用快捷键。聊天打字时、游戏不在前台时不响应。",
                "收起 / 展開左側資訊欄的快捷鍵，預設 F3；填 None 不用快捷鍵。聊天打字時、遊戲不在前景時不回應。",
                "Hotkey to collapse/expand the left info bar (default F3; None disables it). Ignored while typing in chat or when the game is not focused.",
                "左側情報バーを折りたたむ／展開するショートカット（既定 F3、None で無効）。チャット入力中やゲームが前面にないときは反応しません。"));
            changed |= config.Describe(HandToggleKey, D(
                "拉出 / 收回手牌抽屉的快捷键，默认 F4；填 None 不用快捷键。",
                "拉出 / 收回手牌抽屜的快捷鍵，預設 F4；填 None 不用快捷鍵。",
                "Hotkey to open/close the hand drawer (default F4; None disables it).",
                "手札ドロワーを開閉するショートカット（既定 F4、None で無効）。"));
            changed |= config.Describe(RightToggleKey, D(
                "隐藏 / 显示右侧情报栏的快捷键，默认 F5；填 None 不用快捷键。",
                "隱藏 / 顯示右側情報欄的快捷鍵，預設 F5；填 None 不用快捷鍵。",
                "Hotkey to hide/show the right intel bar (default F5; None disables it).",
                "右側インテルバーを隠す／表示するショートカット（既定 F5、None で無効）。"));
            changed |= config.Describe(RightPanelHidden, D(
                "右侧情报栏是否隐藏。按快捷键切换时会自动记下，下一局、重启游戏后保持。",
                "右側情報欄是否隱藏。按快捷鍵切換時會自動記下，下一局、重啟遊戲後保持。",
                "Whether the right intel bar is hidden. Saved automatically when toggled by hotkey and kept across matches and restarts.",
                "右側インテルバーを隠すかどうか。ショートカットで切り替えると自動で保存され、次の対局や再起動後も維持されます。"));
            changed |= config.Describe(LeftPanelCollapsed, D(
                "左侧信息栏是否收起。按快捷键或点把手切换时会自动记下，下一局、重启游戏后保持。",
                "左側資訊欄是否收合。按快捷鍵或點把手切換時會自動記下，下一局、重啟遊戲後保持。",
                "Whether the left info bar is collapsed. Saved automatically when toggled by hotkey or handle and kept across matches and restarts.",
                "左側情報バーを折りたたむかどうか。ショートカットやハンドルで切り替えると自動で保存され、次の対局や再起動後も維持されます。"));
            changed |= config.Describe(AutoCollapse, D(
                "打开商店、转账、进入战斗时（只算自己的）自动收起左侧信息栏，关掉后自动展开（原本就收起的不会被展开）；窗口里手动展开过就不再收。战斗面板不跟着收起。默认开。",
                "打開商店、轉帳、進入戰鬥時（只算自己的）自動收合左側資訊欄，關掉後自動展開（原本就收合的不會被展開）；視窗裡手動展開過就不再收。戰鬥面板不跟著收合。預設開。",
                "Collapse the left info bar automatically while your own shop, transfer (ATM) or battle window is open, and expand it again afterwards (a bar you had collapsed stays collapsed). Expanding it by hand inside the window keeps it open. The battle panel stays visible. On by default.",
                "自分のショップ・送金（ATM）・戦闘のウィンドウが開いている間は左側情報バーを自動で折りたたみ、閉じたら自動で展開します（もともと折りたたんでいた場合は展開しません）。ウィンドウ内で手動で展開すると、そのウィンドウでは再び折りたたみません。戦闘パネルは折りたたまれません。既定はオン。"));
            changed |= config.Describe(GridMap, D(
                "点开顶部的官方小地图时，换成 wiki 式的方格地图：游戏自己的格子图标（对局里格子变了跟着变）、格子编号、P1～P4 出生点，玩家、怪物、Boss 的头像画在所在格子上。关掉就是官方原样。默认开。",
                "點開頂部的官方小地圖時，換成 wiki 式的方格地圖：遊戲自己的格子圖示（對局裡格子變了跟著變）、格子編號、P1～P4 出生點，玩家、怪物、Boss 的頭像畫在所在格子上。關掉就是官方原樣。預設開。",
                "When you open the official minimap at the top, show a wiki-style grid map instead: the game's own tile icons (updated when tiles change during the match), tile numbers, P1–P4 spawn points, and player, monster and boss portraits on their tiles. Turn it off to get the official minimap. On by default.",
                "上部の公式ミニマップを開いたとき、wiki 風のマス目マップに置き換えます。ゲーム本来のマスアイコン（対局中にマスが変わると追従）、マス番号、P1～P4 のスタート地点、プレイヤー・モンスター・ボスの顔アイコンをいるマスに表示します。オフにすると公式のままです。既定はオン。"));
            changed |= config.Describe(HandService, D(
                "手牌抽屉打开时查询手牌：只要拿到本局观战码就会发给第三方观战服务（HandServiceUrl），全 Bot 局也照查。默认开；自己和 Bot 的手牌本地就能看到，服务只补看不到的队友。",
                "手牌抽屜打開時查詢手牌：只要拿到本局觀戰碼就會發給第三方觀戰服務（HandServiceUrl），全 Bot 局也照查。預設開；自己和 Bot 的手牌本機就能看到，服務只補看不到的隊友。",
                "Query hands while the hand drawer is open: as soon as this match's spectator code is available it is sent to a third-party spectating service (HandServiceUrl), all-bot matches included. On by default; your own and bots' hands are read locally, the service only fills in teammates that are hidden.",
                "手札ドロワーを開いている間、この対局の観戦コードが取得でき次第、第三者の観戦サービス（HandServiceUrl）に送信して照会します（全 Bot の対局でも同様）。既定はオン。自分と Bot の手札はローカルで読め、サービスは見えない味方だけを補います。"));
            changed |= config.Describe(HandServiceUrl, D(
                "第三方观战服务地址（来自 HandViewerMod），只在 HandService 打开时使用。",
                "第三方觀戰服務位址（來自 HandViewerMod），只在 HandService 開啟時使用。",
                "Address of the third-party spectating service (from HandViewerMod); only used when HandService is on.",
                "第三者観戦サービスのアドレス（HandViewerMod 由来）。HandService がオンのときのみ使用します。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja) => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
