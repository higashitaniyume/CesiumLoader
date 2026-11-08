using System;

namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 商店成交确认(<see cref="GameEvents.ShopSettled"/>)。
    ///
    /// PVE 商店成交来自 5216(<c>PVEShopBuyS2C</c>), PVP 商店成交来自 5030(<c>ShopBuyS2C</c>)。
    /// 两者字段不完全相同: PVP 没有 <see cref="AssistPlayer"/>(恒 0), 也没有 <see cref="IsClose"/>。
    /// </summary>
    public sealed class ShopSettlement
    {
        /// <summary>成交玩家。</summary>
        public long PlayerId;

        /// <summary>
        /// 本次实际买到的<b>候选槽位下标</b>(与 <see cref="Cards"/> 对齐), 不是卡牌配置 id。
        /// 转账(给队友星币)时为空数组。
        /// </summary>
        public int[] BuyCards;

        /// <summary>成交消息自带的店内卡牌配置 id。</summary>
        public int[] Cards;

        /// <summary>成交消息自带的"已买"标记(与 <see cref="Cards"/> 对齐)。</summary>
        public bool[] Already;

        /// <summary>是否离店。PVP 商店没有该字段, 恒 <c>false</c>。</summary>
        public bool IsClose;

        /// <summary>PVE 转账目标玩家(0 = 不是转账)。PVP 恒 0。</summary>
        public long AssistPlayer;

        /// <summary>是否 PVE 商店成交(<c>false</c> = PVP)。</summary>
        public bool IsPve;

        /// <summary>
        /// 这条成交消息的下行序号(用于和候选 action 的 <c>Sn</c> 对账)。
        /// 读不到时为 0; 读取方式见 <see cref="GameEvents.DownSn(int)"/>。
        /// </summary>
        public long DownSn;

        /// <summary>
        /// 把 <see cref="BuyCards"/>(槽位下标)换算成卡牌配置 id。
        /// 下标越界的位置填 0, 不会抛异常。
        /// </summary>
        public int[] ResolveBoughtCardIds()
        {
            if (BuyCards == null || BuyCards.Length == 0) return Array.Empty<int>();
            var ids = new int[BuyCards.Length];
            for (int i = 0; i < BuyCards.Length; i++)
            {
                int slot = BuyCards[i];
                ids[i] = (Cards != null && slot >= 0 && slot < Cards.Length) ? Cards[slot] : 0;
            }
            return ids;
        }

        public override string ToString()
        {
            return (IsPve ? "PVE" : "PVP") + " 商店成交(pid=" + PlayerId +
                   ", 买=" + (BuyCards == null ? 0 : BuyCards.Length) +
                   ", 转账=" + AssistPlayer +
                   ", 离店=" + IsClose +
                   ", downSn=" + DownSn + ")";
        }
    }
}
