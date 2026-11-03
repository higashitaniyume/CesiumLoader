using System;
using System.Collections.Generic;

namespace StarCoinShopMod
{
    /// <summary>待购商品候选项快照（纯数据模型，方便单元测试与离线编排）。</summary>
    public sealed class ShopItemCandidate
    {
        public int SlotIndex { get; set; } = 1;
        public int GoodsId { get; set; }
        public string Name { get; set; }
        public int ShopTabType { get; set; }
        public int CurrencyId { get; set; }
        public int Price { get; set; }
        public int RemainingCount { get; set; }
        public bool IsSoldOut { get; set; }
        public bool IsOwned { get; set; }
    }

    /// <summary>一键购买计划执行结果。</summary>
    public sealed class PurchasePlan
    {
        public List<ShopItemCandidate> ItemsToBuy { get; } = new List<ShopItemCandidate>();
        public List<ShopItemCandidate> SkippedDueToBalance { get; } = new List<ShopItemCandidate>();
        public Dictionary<int, int> TotalCostByCurrency { get; } = new Dictionary<int, int>();
        public bool AlreadyCompleted { get; set; }
    }

    /// <summary>星币商店购买编排器（纯逻辑）。</summary>
    public static class ShopPurchasePlanner
    {
        /// <summary>
        /// 根据当前货架商品与玩家持有货币余额，编排购买队列。
        /// 规则：
        ///   1. 排除玩家未勾选的槽位（slotFilter）。
        ///   2. 排除已售罄（IsSoldOut）、已拥有装扮（IsOwned）及剩余数量为 0 的商品。
        ///   3. 免费商品（Price == 0）优先加入购买队列。
        ///   4. 检查货币余额，按顺序扣减可用余额，余额充足才加入队列，不足则记录跳过。
        /// </summary>
        public static PurchasePlan PlanPurchase(
            IEnumerable<ShopItemCandidate> items,
            Func<int, int> balanceProvider,
            Func<int, bool> slotFilter = null)
        {
            var plan = new PurchasePlan();
            if (items == null)
            {
                plan.AlreadyCompleted = true;
                return plan;
            }

            var eligible = new List<ShopItemCandidate>();
            int totalCandidates = 0;

            foreach (var item in items)
            {
                if (item == null) continue;
                if (slotFilter != null && !slotFilter(item.SlotIndex))
                {
                    continue;
                }
                totalCandidates++;
                if (item.IsSoldOut || item.IsOwned || item.RemainingCount <= 0)
                {
                    continue;
                }
                eligible.Add(item);
            }

            if (eligible.Count == 0)
            {
                plan.AlreadyCompleted = totalCandidates > 0;
                return plan;
            }

            // 排序：免费商品排在最前，付费商品保持原始货架展示顺序
            eligible.Sort((a, b) =>
            {
                bool aFree = a.Price <= 0;
                bool bFree = b.Price <= 0;
                if (aFree && !bFree) return -1;
                if (!aFree && bFree) return 1;
                return 0;
            });

            var remainingBalanceCache = new Dictionary<int, int>();

            foreach (var item in eligible)
            {
                int buyCount = Math.Max(1, item.RemainingCount);
                int cost = item.Price * buyCount;

                if (item.Price <= 0)
                {
                    plan.ItemsToBuy.Add(item);
                    continue;
                }

                int currency = item.CurrencyId;
                if (!remainingBalanceCache.TryGetValue(currency, out int balance))
                {
                    balance = balanceProvider != null ? balanceProvider(currency) : 0;
                    remainingBalanceCache[currency] = balance;
                }

                if (balance >= cost)
                {
                    remainingBalanceCache[currency] = balance - cost;
                    plan.ItemsToBuy.Add(item);
                    int currentTotal;
                    plan.TotalCostByCurrency.TryGetValue(currency, out currentTotal);
                    plan.TotalCostByCurrency[currency] = currentTotal + cost;
                }
                else
                {
                    plan.SkippedDueToBalance.Add(item);
                }
            }

            return plan;
        }
    }
}
