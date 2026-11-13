using CesiumLoader.SDK.Configuration;

namespace PvePanelMod
{
    /// <summary>config.json 里各字段的多语言说明（照 CesiumLoader 内置 mod 的规矩：简中、繁中、英文、日文）。</summary>
    internal static class PanelConfigDocs
    {
        public const string HandService = "HandService";
        public const string HandServiceUrl = "HandServiceUrl";
        public const string DefaultServiceUrl = "https://aspac.hiynet.com/";
        public const string LeftToggleKey = "LeftToggleKey", HandToggleKey = "HandToggleKey", RightToggleKey = "RightToggleKey";
        public const string RightPanelHidden = "RightPanelHidden";
        public const string LeftPanelCollapsed = "LeftPanelCollapsed";
        public const string UiScale = "UiScale";
        public const string DefaultLeftKey = "F3", DefaultHandKey = "F4", DefaultRightKey = "F5";

        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
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
            changed |= config.Describe(HandService, D(
                "手牌抽屉查询真人队友的手牌：会把本局观战码发给第三方观战服务（HandServiceUrl）。默认关；自己和 Bot 的手牌不需要它，本地就能看到。",
                "手牌抽屜查詢真人隊友的手牌：會把本局觀戰碼發給第三方觀戰服務（HandServiceUrl）。預設關；自己和 Bot 的手牌不需要它，本機就能看到。",
                "Let the hand drawer look up human teammates' hands by sending this match's spectator code to a third-party spectating service (HandServiceUrl). Off by default; your own and bots' hands are read locally without it.",
                "手札ドロワーで人間の味方の手札を照会します。この対局の観戦コードを第三者の観戦サービス（HandServiceUrl）に送信します。既定はオフ。自分と Bot の手札はこれなしでローカルに表示されます。"));
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
