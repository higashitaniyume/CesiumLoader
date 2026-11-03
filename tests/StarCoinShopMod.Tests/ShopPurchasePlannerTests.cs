using System.Collections.Generic;
using Xunit;

namespace StarCoinShopMod.Tests
{
    public class ShopPurchasePlannerTests
    {
        [Fact]
        public void PlanPurchase_NullOrEmptyItems_ReturnsCompleted()
        {
            var plan1 = ShopPurchasePlanner.PlanPurchase(null, _ => 1000);
            Assert.True(plan1.AlreadyCompleted);
            Assert.Empty(plan1.ItemsToBuy);

            var plan2 = ShopPurchasePlanner.PlanPurchase(new List<ShopItemCandidate>(), _ => 1000);
            Assert.Empty(plan2.ItemsToBuy);
        }

        [Fact]
        public void PlanPurchase_AllSoldOutOrOwned_ReturnsAlreadyCompleted()
        {
            var items = new List<ShopItemCandidate>
            {
                new ShopItemCandidate { GoodsId = 1, Name = "鸭子", Price = 50, RemainingCount = 0, IsSoldOut = true },
                new ShopItemCandidate { GoodsId = 2, Name = "服装", Price = 50, RemainingCount = 1, IsOwned = true },
                new ShopItemCandidate { GoodsId = 3, Name = "电脑", Price = 300, RemainingCount = 0, IsSoldOut = false }
            };

            var plan = ShopPurchasePlanner.PlanPurchase(items, _ => 10000);
            Assert.True(plan.AlreadyCompleted);
            Assert.Empty(plan.ItemsToBuy);
            Assert.Empty(plan.SkippedDueToBalance);
        }

        [Fact]
        public void PlanPurchase_FreeItems_PrioritizedAndAlwaysIncluded()
        {
            var items = new List<ShopItemCandidate>
            {
                new ShopItemCandidate { GoodsId = 101, Name = "平板", CurrencyId = 1, Price = 300, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 102, Name = "免费鸭", CurrencyId = 0, Price = 0, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 103, Name = "手机", CurrencyId = 1, Price = 50, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 104, Name = "免费衣服", CurrencyId = 0, Price = 0, RemainingCount = 1 },
            };

            // 0 余额时，免费商品依然能够成功规划
            var plan = ShopPurchasePlanner.PlanPurchase(items, _ => 0);
            Assert.Equal(2, plan.ItemsToBuy.Count);
            Assert.Equal(102, plan.ItemsToBuy[0].GoodsId);
            Assert.Equal(104, plan.ItemsToBuy[1].GoodsId);
            Assert.Equal(2, plan.SkippedDueToBalance.Count);
        }

        [Fact]
        public void PlanPurchase_TypicalEightItems_WithSufficientBalance_PlansAll()
        {
            // 模拟用户截图中的 8 个商品
            var items = new List<ShopItemCandidate>
            {
                new ShopItemCandidate { GoodsId = 1, Name = "浴室鸭", CurrencyId = 0, Price = 0, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 2, Name = "居家服", CurrencyId = 0, Price = 0, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 3, Name = "智能手机", CurrencyId = 1, Price = 50, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 4, Name = "浴室鸭(星币)", CurrencyId = 1, Price = 50, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 5, Name = "新款平板电脑", CurrencyId = 1, Price = 300, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 6, Name = "小熊玩偶", CurrencyId = 1, Price = 300, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 7, Name = "阳炎徽章", CurrencyId = 1, Price = 300, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 8, Name = "山峦徽章", CurrencyId = 1, Price = 300, RemainingCount = 1 }
            };

            // 用户持有 8387 星币（充足）
            var plan = ShopPurchasePlanner.PlanPurchase(items, currencyId => currencyId == 1 ? 8387 : 0);

            Assert.Equal(8, plan.ItemsToBuy.Count);
            Assert.Empty(plan.SkippedDueToBalance);
            // 免费 2 个 + 50*2 + 300*4 = 100 + 1200 = 1300 星币
            Assert.Equal(1300, plan.TotalCostByCurrency[1]);
        }

        [Fact]
        public void PlanPurchase_PartialBalance_BuysAffordableAndSkipsRemaining()
        {
            var items = new List<ShopItemCandidate>
            {
                new ShopItemCandidate { GoodsId = 1, Name = "免费礼品", CurrencyId = 0, Price = 0, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 2, Name = "小件", CurrencyId = 1, Price = 50, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 3, Name = "大件A", CurrencyId = 1, Price = 300, RemainingCount = 1 },
                new ShopItemCandidate { GoodsId = 4, Name = "大件B", CurrencyId = 1, Price = 300, RemainingCount = 1 }
            };

            // 只有 100 星币：能买免费礼品(0)和小件(50)，剩余 50 买不起 300 的大件
            var plan = ShopPurchasePlanner.PlanPurchase(items, currencyId => currencyId == 1 ? 100 : 0);

            Assert.Equal(2, plan.ItemsToBuy.Count);
            Assert.Equal(1, plan.ItemsToBuy[0].GoodsId);
            Assert.Equal(2, plan.ItemsToBuy[1].GoodsId);
            Assert.Equal(2, plan.SkippedDueToBalance.Count);
            Assert.Equal(3, plan.SkippedDueToBalance[0].GoodsId);
            Assert.Equal(4, plan.SkippedDueToBalance[1].GoodsId);
            Assert.Equal(50, plan.TotalCostByCurrency[1]);
        }

        [Fact]
        public void PlanPurchase_MultiCountItem_CalculatesCostCorrectly()
        {
            var items = new List<ShopItemCandidate>
            {
                new ShopItemCandidate { GoodsId = 10, Name = "批量道具", CurrencyId = 1, Price = 20, RemainingCount = 5 }
            };

            // 5 * 20 = 100 星币
            var plan = ShopPurchasePlanner.PlanPurchase(items, _ => 100);
            Assert.Single(plan.ItemsToBuy);
            Assert.Equal(100, plan.TotalCostByCurrency[1]);

            var failPlan = ShopPurchasePlanner.PlanPurchase(items, _ => 99);
            Assert.Empty(failPlan.ItemsToBuy);
            Assert.Single(failPlan.SkippedDueToBalance);
        }

        [Fact]
        public void PlanPurchase_SlotFilter_DefaultSlots1278_OnlyPurchasesSlots1278()
        {
            var items = new List<ShopItemCandidate>
            {
                new ShopItemCandidate { SlotIndex = 1, GoodsId = 1, Name = "浴室鸭(免费)", CurrencyId = 0, Price = 0, RemainingCount = 1 },
                new ShopItemCandidate { SlotIndex = 2, GoodsId = 2, Name = "居家服(免费)", CurrencyId = 0, Price = 0, RemainingCount = 1 },
                new ShopItemCandidate { SlotIndex = 3, GoodsId = 3, Name = "智能手机", CurrencyId = 1, Price = 50, RemainingCount = 1 },
                new ShopItemCandidate { SlotIndex = 4, GoodsId = 4, Name = "浴室鸭(50星币)", CurrencyId = 1, Price = 50, RemainingCount = 1 },
                new ShopItemCandidate { SlotIndex = 5, GoodsId = 5, Name = "平板电脑", CurrencyId = 1, Price = 300, RemainingCount = 1 },
                new ShopItemCandidate { SlotIndex = 6, GoodsId = 6, Name = "小熊玩偶", CurrencyId = 1, Price = 300, RemainingCount = 1 },
                new ShopItemCandidate { SlotIndex = 7, GoodsId = 7, Name = "阳炎徽章", CurrencyId = 1, Price = 300, RemainingCount = 1 },
                new ShopItemCandidate { SlotIndex = 8, GoodsId = 8, Name = "山峦徽章", CurrencyId = 1, Price = 300, RemainingCount = 1 }
            };

            // 默认勾选 1, 2, 7, 8
            var plan = ShopPurchasePlanner.PlanPurchase(
                items,
                currencyId => 10000,
                slot => slot == 1 || slot == 2 || slot == 7 || slot == 8);

            Assert.Equal(4, plan.ItemsToBuy.Count);
            // 免费 2 个在前，徽章 2 个在后
            Assert.Equal(1, plan.ItemsToBuy[0].GoodsId);
            Assert.Equal(2, plan.ItemsToBuy[1].GoodsId);
            Assert.Equal(7, plan.ItemsToBuy[2].GoodsId);
            Assert.Equal(8, plan.ItemsToBuy[3].GoodsId);
            // 总花费仅 300 + 300 = 600
            Assert.Equal(600, plan.TotalCostByCurrency[1]);
        }

        [Fact]
        public void PlanPurchase_SlotFilter_SelectedSlotsSoldOut_ReturnsAlreadyCompleted()
        {
            var items = new List<ShopItemCandidate>
            {
                new ShopItemCandidate { SlotIndex = 1, GoodsId = 1, Name = "浴室鸭(免费)", Price = 0, RemainingCount = 0, IsSoldOut = true },
                new ShopItemCandidate { SlotIndex = 2, GoodsId = 2, Name = "居家服(免费)", Price = 0, RemainingCount = 0, IsSoldOut = true },
                new ShopItemCandidate { SlotIndex = 3, GoodsId = 3, Name = "智能手机", Price = 50, RemainingCount = 1, IsSoldOut = false },
                new ShopItemCandidate { SlotIndex = 7, GoodsId = 7, Name = "阳炎徽章", Price = 300, RemainingCount = 0, IsSoldOut = true },
                new ShopItemCandidate { SlotIndex = 8, GoodsId = 8, Name = "山峦徽章", Price = 300, RemainingCount = 0, IsSoldOut = true }
            };

            // 勾选 1, 2, 7, 8，虽然第 3 格未售罄，但玩家没勾选第 3 格，因此所选商品已全部售罄完成
            var plan = ShopPurchasePlanner.PlanPurchase(
                items,
                _ => 10000,
                slot => slot == 1 || slot == 2 || slot == 7 || slot == 8);

            Assert.Empty(plan.ItemsToBuy);
            Assert.True(plan.AlreadyCompleted);
        }

        [Fact]
        public void PlanPurchase_SlotFilter_NoSlotsSelected_ReturnsEmptyAndNotCompleted()
        {
            var items = new List<ShopItemCandidate>
            {
                new ShopItemCandidate { SlotIndex = 1, GoodsId = 1, Name = "浴室鸭", Price = 0, RemainingCount = 1 }
            };

            var plan = ShopPurchasePlanner.PlanPurchase(items, _ => 10000, _ => false);
            Assert.Empty(plan.ItemsToBuy);
            Assert.False(plan.AlreadyCompleted);
        }
    }
}
