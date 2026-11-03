using CesiumLoader.SDK.Configuration;

namespace StarCoinShopMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class StarCoinShopConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("enabled", D(
                "总开关：开启星币商店一键购买功能。",
                "總開關：開啟星幣商店一鍵購買功能。",
                "Master switch: enable one-click purchase in the Star Coin shop.",
                "全体スイッチ：スターコインショップの一括購入機能を有効化します。"));
            changed |= config.Describe("cooldownSeconds", D(
                "每个商品购买之间的冷却间隔（秒），默认 0.8 秒，避免连续请求过快触发限制。",
                "每個商品購買之間的冷卻間隔（秒），預設 0.8 秒，避免連續請求過快觸發限制。",
                "Cooldown interval in seconds between individual purchases (default 0.8s).",
                "各商品の購入間隔（秒、既定 0.8 秒）。"));
            changed |= config.Describe("quickBuyKey", D(
                "星币商店界面一键购买快捷键，默认 F。",
                "星幣商店介面一鍵購買快捷鍵，預設 F。",
                "Shortcut key to trigger one-click purchase in the shop panel (default F).",
                "ショップ画面での一括購入ショートカットキー（既定 F）。"));
            changed |= config.Describe("showButton", D(
                "是否在星币商店标题旁显示「一键购买」按钮。",
                "是否在星幣商店標題旁顯示「一鍵購買」按鈕。",
                "Show the 'One-Click Buy' button next to the Star Coin Shop title.",
                "スターコインショップの見出し横に「一括購入」按钮を表示するかどうか。"));
            changed |= config.Describe("buttonOffsetX", D(
                "「一键购买」按钮水平微调（像素，负数向左，正数向右）。",
                "「一鍵購買」按鈕水平微調（像素，負數向左，正數向右）。",
                "Horizontal offset for the One-Click Buy button in pixels (negative moves left, positive moves right).",
                "「一括購入」ボタンの水平オフセット（ピクセル、負数は左、正数は右）。"));
            changed |= config.Describe("notifyOnComplete", D(
                "购买全部完成或终止时是否弹出游戏内通知提示。",
                "購買全部完成或終止時是否彈出遊戲內通知提示。",
                "Whether to show in-game notification when batch purchase completes.",
                "一括購入の完了または中断時にゲーム内通知を表示するかどうか。"));
            changed |= config.Describe("buySlot1", D(
                "自动购买第 1 格商品（通常为每日免费道具 1，默认开启）。",
                "自動購買第 1 格商品（通常為每日免費道具 1，預設開啟）。",
                "Auto-buy slot 1 item (usually daily free gift 1, default enabled).",
                "第 1 スロットの商品を自動購入（通常はデイリー無料ギフト 1、既定で有効）。"));
            changed |= config.Describe("buySlot2", D(
                "自动购买第 2 格商品（通常为每日免费道具 2，默认开启）。",
                "自動購買第 2 格商品（通常為每日免費道具 2，預設開啟）。",
                "Auto-buy slot 2 item (usually daily free gift 2, default enabled).",
                "第 2 スロットの商品を自動購入（通常はデイリー無料ギフト 2、既定で有効）。"));
            changed |= config.Describe("buySlot3", D(
                "自动购买第 3 格商品（通常为 50 星币道具 1，默认关闭）。",
                "自動購買第 3 格商品（通常為 50 星幣道具 1，預設關閉）。",
                "Auto-buy slot 3 item (usually 50 star coins gift 1, default disabled).",
                "第 3 スロットの商品を自動購入（通常は 50 スターコインギフト 1、既定で無効）。"));
            changed |= config.Describe("buySlot4", D(
                "自动购买第 4 格商品（通常为 50 星币道具 2，默认关闭）。",
                "自動購買第 4 格商品（通常為 50 星幣道具 2，預設關閉）。",
                "Auto-buy slot 4 item (usually 50 star coins gift 2, default disabled).",
                "第 4 スロットの商品を自動購入（通常は 50 スターコインギフト 2、既定で無効）。"));
            changed |= config.Describe("buySlot5", D(
                "自动购买第 5 格商品（通常为 300 星币道具 1，默认关闭）。",
                "自動購買第 5 格商品（通常為 300 星幣道具 1，預設關閉）。",
                "Auto-buy slot 5 item (usually 300 star coins gift 1, default disabled).",
                "第 5 スロットの商品を自動購入（通常は 300 スターコインギフト 1、既定で無効）。"));
            changed |= config.Describe("buySlot6", D(
                "自动购买第 6 格商品（通常为 300 星币道具 2，默认关闭）。",
                "自動購買第 6 格商品（通常為 300 星幣道具 2，預設關閉）。",
                "Auto-buy slot 6 item (usually 300 star coins gift 2, default disabled).",
                "第 6 スロットの商品を自動購入（通常は 300 スターコインギフト 2、既定で無効）。"));
            changed |= config.Describe("buySlot7", D(
                "自动购买第 7 格商品（通常为 300 星币徽章 1，默认开启）。",
                "自動購買第 7 格商品（通常為 300 星幣徽章 1，預設開啟）。",
                "Auto-buy slot 7 item (usually 300 star coins badge 1, default enabled).",
                "第 7 スロットの商品を自動購入（通常は 300 スターコインバッジ 1、既定で有効）。"));
            changed |= config.Describe("buySlot8", D(
                "自动购买第 8 格商品（通常为 300 星币徽章 2，默认开启）。",
                "自動購買第 8 格商品（通常為 300 星幣徽章 2，預設開啟）。",
                "Auto-buy slot 8 item (usually 300 star coins badge 2, default enabled).",
                "第 8 スロットの商品を自動購入（通常は 300 スターコインバッジ 2、既定で有効）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
