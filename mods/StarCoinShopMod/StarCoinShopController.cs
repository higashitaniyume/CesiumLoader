using System;
using System.Collections;
using System.Collections.Generic;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Scheduling;
using CesiumLoader.SDK.UserInterface;
using FairyGUI;
using GameLogic;
using Tools;
using UI;
using UnityEngine;

namespace StarCoinShopMod
{
    public sealed class StarCoinShopController : ModBase
    {
        private readonly StarCoinShopConfig _cfg = new StarCoinShopConfig();
        private bool _isPurchasing;
        private ProductRecommendationPanel _injectedPanel;
        private GComponent _quickBuyButton;
        private GTextField _buttonText;
        private GGraph _buttonBg;

        public override string Name => "星币商店助手";
        public override string Version => "1.0.0";
        public override string Author => "CesiumLoader";
        public override string Description => "主界面商城星币商店一键顺序购买全部商品，带可配置的冷却间隔。";

        public override void OnInitialize()
        {
            ReadConfig(isInitial: true);
            Context?.RegisterCleanup(OnCleanup);
            Log.Info("星币商店助手已启用：进入商城推荐面板可一键顺序购买 8 个每日商品。");
        }

        public override void OnUpdate()
        {
            if (!_cfg.Enabled) return;

            var mgr = SimpleSingletonProvider<UIManager>.inst;
            if (mgr == null) return;

            var currentPanel = mgr.currentPanel as ProductRecommendationPanel;
            bool isShopOpen = currentPanel != null && currentPanel.IsOpen() && currentPanel.UIObject != null;

            if (!isShopOpen)
            {
                if (_injectedPanel != null)
                {
                    _injectedPanel = null;
                    _quickBuyButton = null;
                    _buttonText = null;
                    _buttonBg = null;
                }
                return;
            }

            if (_cfg.ShowButton && (_injectedPanel != currentPanel || _quickBuyButton == null || _quickBuyButton.parent == null))
            {
                EnsureButtonInjected(currentPanel);
            }

            if (!_isPurchasing && InputService.IsKeyPressed(_cfg.ParsedQuickBuyKey))
            {
                TriggerQuickBuy(currentPanel);
            }
        }

        private void ReadConfig(bool isInitial = false)
        {
            if (Config != null)
            {
                if (isInitial)
                {
                    StarCoinShopConfigDocs.Apply(Config);
                    Config.SetDefault("enabled", true);
                    Config.SetDefault("cooldownSeconds", 0.8f);
                    Config.SetDefault("quickBuyKey", "F");
                    Config.SetDefault("showButton", true);
                    Config.SetDefault("buttonOffsetX", 0f);
                    Config.SetDefault("notifyOnComplete", true);
                    Config.SetDefault("buySlot1", true);
                    Config.SetDefault("buySlot2", true);
                    Config.SetDefault("buySlot3", false);
                    Config.SetDefault("buySlot4", false);
                    Config.SetDefault("buySlot5", false);
                    Config.SetDefault("buySlot6", false);
                    Config.SetDefault("buySlot7", true);
                    Config.SetDefault("buySlot8", true);
                }

                _cfg.Enabled = Config.GetBool("enabled", true);
                _cfg.CooldownSeconds = Math.Max(0.2f, Math.Min(5.0f, Config.GetFloat("cooldownSeconds", 0.8f)));
                _cfg.QuickBuyKey = Config.GetString("quickBuyKey", "F");
                _cfg.ShowButton = Config.GetBool("showButton", true);
                _cfg.ButtonOffsetX = Config.GetFloat("buttonOffsetX", 0f);
                _cfg.NotifyOnComplete = Config.GetBool("notifyOnComplete", true);
                _cfg.BuySlot1 = Config.GetBool("buySlot1", Config.GetBool("buyItem1", true));
                _cfg.BuySlot2 = Config.GetBool("buySlot2", Config.GetBool("buyItem2", true));
                _cfg.BuySlot3 = Config.GetBool("buySlot3", Config.GetBool("buyItem3", false));
                _cfg.BuySlot4 = Config.GetBool("buySlot4", Config.GetBool("buyItem4", false));
                _cfg.BuySlot5 = Config.GetBool("buySlot5", Config.GetBool("buyItem5", false));
                _cfg.BuySlot6 = Config.GetBool("buySlot6", Config.GetBool("buyItem6", false));
                _cfg.BuySlot7 = Config.GetBool("buySlot7", Config.GetBool("buyItem7", true));
                _cfg.BuySlot8 = Config.GetBool("buySlot8", Config.GetBool("buyItem8", true));

                if (isInitial) Config.SaveIfDirty();
            }
        }

        private void EnsureButtonInjected(ProductRecommendationPanel panel)
        {
            try
            {
                var comGoods = panel?.UIObject?.com_Goods;
                if (comGoods == null) return;

                var txtTitle = comGoods.txt_Title;
                var txtTime = comGoods.txt_Time;

                // 计算标题「星币商店」的文字实际左边缘（处理对齐与实际文本宽度）
                float titleVisualLeft;
                if (txtTitle != null)
                {
                    float tw = txtTitle.textWidth > 0 ? txtTitle.textWidth : 160f;
                    if (txtTitle.align == AlignType.Right)
                    {
                        titleVisualLeft = txtTitle.x + txtTitle.width - tw;
                    }
                    else if (txtTitle.align == AlignType.Center)
                    {
                        titleVisualLeft = txtTitle.x + (txtTitle.width - tw) / 2f;
                    }
                    else
                    {
                        titleVisualLeft = txtTitle.x;
                    }
                }
                else
                {
                    titleVisualLeft = 520f;
                }

                // 按钮放置在标题左侧，留出 30 像素空隙
                const float buttonWidth = 110f;
                float x = titleVisualLeft - buttonWidth - 30f + _cfg.ButtonOffsetX;
                float y = (txtTitle != null) ? txtTitle.y + (txtTitle.height - 32f) / 2f : 10f;

                // 避免与倒计时文字（刷新倒计时:...）重叠：必须使用 textWidth（实际文字宽度），不能用容器 width（常为占位大矩形）
                if (txtTime != null)
                {
                    float timeRight = txtTime.x + (txtTime.textWidth > 0 ? txtTime.textWidth : 200f) + 12f;
                    if (x < timeRight)
                    {
                        x = timeRight;
                    }
                }

                Log.Info($"[StarCoinShop] 按钮布局: titleVisualLeft={titleVisualLeft:F1}, buttonX={x:F1}, buttonY={y:F1}");

                var existing = comGoods.GetChild("btn_QuickBuy") as GComponent;
                if (existing != null)
                {
                    _quickBuyButton = existing;
                    _buttonBg = existing.GetChild("bg") as GGraph;
                    _buttonText = existing.GetChild("title") as GTextField;
                    _quickBuyButton.SetPosition(x, y, 0f);
                    _injectedPanel = panel;
                    return;
                }

                _quickBuyButton = new GComponent { name = "btn_QuickBuy" };
                _quickBuyButton.SetSize(buttonWidth, 32f);

                _buttonBg = new GGraph { name = "bg" };
                _buttonBg.SetSize(buttonWidth, 32f);
                _buttonBg.DrawRoundRect(buttonWidth, 32f, new Color(0.2f, 0.7f, 0.35f, 0.95f), new float[] { 6f, 6f, 6f, 6f });
                _quickBuyButton.AddChild(_buttonBg);

                _buttonText = new GTextField
                {
                    name = "title",
                    text = "一键购买",
                    textFormat = new TextFormat { size = 18, bold = true, color = Color.white },
                    align = AlignType.Center,
                    verticalAlign = VertAlignType.Middle,
                    touchable = false
                };
                _buttonText.SetSize(buttonWidth, 32f);
                _quickBuyButton.AddChild(_buttonText);

                _quickBuyButton.touchable = true;
                _quickBuyButton.onClick.Add(() => TriggerQuickBuy(panel));

                comGoods.AddChild(_quickBuyButton);
                _quickBuyButton.SetPosition(x, y, 0f);

                Log.Info($"[StarCoinShop] 按钮布局完成: titleVisualLeft={titleVisualLeft}, buttonX={x}, buttonY={y}");

                _injectedPanel = panel;
            }
            catch (Exception e)
            {
                Log.Warn("注入一键购买按钮异常: " + e.Message);
            }
        }

        public void TriggerQuickBuy(ProductRecommendationPanel panel)
        {
            if (_isPurchasing) return;
            CoroutineService.StartCoroutine(RunPurchaseRoutine(panel), Context);
        }

        private IEnumerator RunPurchaseRoutine(ProductRecommendationPanel panel)
        {
            _isPurchasing = true;
            UpdateButtonUi(purchasing: true, "正在检索...");

            var store = SimpleSingletonProvider<GameLogicManager>.inst?.store;
            var bag = SimpleSingletonProvider<GameLogicManager>.inst?.bag;
            if (store == null || bag == null)
            {
                Notify("无法获取商店或背包数据");
                UpdateButtonUi(purchasing: false, "一键购买");
                _isPurchasing = false;
                yield break;
            }

            var dailyGoods = store.GetDailyRefresh();
            if (dailyGoods == null || dailyGoods.Count == 0)
            {
                Notify("未获取到星币商店商品数据");
                UpdateButtonUi(purchasing: false, "一键购买");
                _isPurchasing = false;
                yield break;
            }

            // 规范化货架顺序，使 8 个商品严格对应槽位 1~8：
            // 格 1,2: 每日免费道具 (Price == 0)
            // 格 3,4: 50 星币道具 (Price == 50)
            // 格 5,6: 300 星币礼物 (Price == 300, 非徽章)
            // 格 7,8: 300 星币徽章 (Price == 300, 徽章)
            var sortedGoods = new List<DailRefreshGoods>(dailyGoods);
            sortedGoods.Sort(CanonicalSlotCompare);

            var candidates = new List<ShopItemCandidate>();
            for (int i = 0; i < sortedGoods.Count; i++)
            {
                var g = sortedGoods[i];
                if (g == null) continue;
                int slotIndex = i + 1;
                candidates.Add(new ShopItemCandidate
                {
                    SlotIndex = slotIndex,
                    GoodsId = g.goodsId,
                    Name = g.GetName(),
                    ShopTabType = (int)g.shopTabType,
                    CurrencyId = g.currencyID,
                    Price = g.SalePrice,
                    RemainingCount = g.RemainingNum(),
                    IsSoldOut = g.SellOut(),
                    IsOwned = g.IsOwn()
                });
            }

            var plan = ShopPurchasePlanner.PlanPurchase(
                candidates,
                (currencyId) => bag.GetItemCount(currencyId),
                (slot) => _cfg.IsSlotEnabled(slot));

            if (plan.ItemsToBuy.Count == 0)
            {
                if (plan.AlreadyCompleted)
                {
                    Notify("星币商店所选商品已全部购买或已售罄");
                }
                else if (plan.SkippedDueToBalance.Count > 0)
                {
                    Notify("星币余额不足，无法购买所选商品");
                }
                else
                {
                    Notify("当前未勾选任何待购买的商品槽位");
                }
                UpdateButtonUi(purchasing: false, "一键购买");
                _isPurchasing = false;
                yield break;
            }

            int successCount = 0;
            int failCount = 0;

            for (int i = 0; i < plan.ItemsToBuy.Count; i++)
            {
                var item = plan.ItemsToBuy[i];
                UpdateButtonUi(purchasing: true, $"购买中 ({i + 1}/{plan.ItemsToBuy.Count})");

                if (item.Price > 0 && item.CurrencyId > 0)
                {
                    int currentBalance = bag.GetItemCount(item.CurrencyId);
                    if (currentBalance < item.Price * item.RemainingCount)
                    {
                        Log.Warn($"星币不足，跳过购买 {item.Name} (需要 {item.Price * item.RemainingCount}，持有 {currentBalance})");
                        failCount++;
                        continue;
                    }
                }

                var req = store.RequestMallBuy(item.ShopTabType, item.GoodsId, item.RemainingCount, 0);

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (req != null && req.keepWaiting && DateTime.UtcNow < deadline)
                {
                    yield return null;
                }

                if (req != null && !req.keepWaiting && req.errId == 0)
                {
                    successCount++;
                    Log.Info($"成功购买商品: {item.Name} (id={item.GoodsId}) x{item.RemainingCount}");
                }
                else
                {
                    failCount++;
                    int err = req != null ? req.errId : -1;
                    Log.Warn($"购买商品失败: {item.Name} (id={item.GoodsId}), errId={err}");
                }

                if (_cfg.CooldownSeconds > 0f)
                {
                    yield return new RoutineWaitSeconds(_cfg.CooldownSeconds);
                }
            }

            try
            {
                store.signal.refreshCurShelf.Dispatch(0);
            }
            catch { }

            UpdateButtonUi(purchasing: false, "一键购买");
            _isPurchasing = false;

            if (_cfg.NotifyOnComplete)
            {
                string msg = $"星币商店购买完成：成功 {successCount} 件";
                if (failCount > 0) msg += $"，跳过/失败 {failCount} 件";
                Notify(msg);
            }
        }

        private void UpdateButtonUi(bool purchasing, string text)
        {
            if (_quickBuyButton != null)
            {
                _quickBuyButton.touchable = !purchasing;
            }
            if (_buttonText != null)
            {
                _buttonText.text = text;
            }
            if (_buttonBg != null)
            {
                var color = purchasing
                    ? new Color(0.5f, 0.5f, 0.5f, 0.95f)
                    : new Color(0.2f, 0.7f, 0.35f, 0.95f);
                _buttonBg.DrawRoundRect(110f, 32f, color, new float[] { 6f, 6f, 6f, 6f });
            }
        }

        private void Notify(string message)
        {
            Log.Info(message);
            try
            {
                UiService.Notify(message, UiNotificationLevel.Info, 3.0f, Context);
            }
            catch { }
        }

        private static int CanonicalSlotCompare(DailRefreshGoods a, DailRefreshGoods b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            bool aBadge = IsBadgeItem(a);
            bool bBadge = IsBadgeItem(b);
            int aTier = a.SalePrice == 0 ? 0 : (a.SalePrice == 50 ? 1 : (aBadge ? 3 : 2));
            int bTier = b.SalePrice == 0 ? 0 : (b.SalePrice == 50 ? 1 : (bBadge ? 3 : 2));
            if (aTier != bTier) return aTier.CompareTo(bTier);

            return a.goodsId.CompareTo(b.goodsId);
        }

        private static bool IsBadgeItem(DailRefreshGoods g)
        {
            if (g == null) return false;
            if (g.goodsConfig != null && g.goodsConfig.ItemID >= 78000 && g.goodsConfig.ItemID < 79000)
            {
                return true;
            }
            if (g.itemConfig != null && g.itemConfig.ItemType == ItemType.Material)
            {
                return true;
            }
            return false;
        }

        private void OnCleanup()
        {
            if (_quickBuyButton != null)
            {
                try { _quickBuyButton.Dispose(); } catch { }
                _quickBuyButton = null;
            }
            _injectedPanel = null;
        }
    }
}
