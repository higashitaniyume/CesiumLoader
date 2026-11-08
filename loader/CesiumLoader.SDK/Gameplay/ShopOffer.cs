using System;

namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 一次进店报价的完整内容(<see cref="GameEvents.ShopOffered"/>)。
    ///
    /// 来源是 <c>PredictActionS2C</c> 里的动作: 5215 解 <c>PVEShopBuyC2S</c>, 5029 解 <c>ShopBuyC2S</c>。
    /// 与只给 id 列表的 <see cref="GameEvents.ShopCandidates"/> 相比, 这里保留价格、折扣、
    /// 天赋免费与已买标记, 一个字段都不丢。
    /// </summary>
    public sealed class ShopOffer
    {
        /// <summary>报价玩家。</summary>
        public long PlayerId;

        /// <summary>动作序号(服务器会重复广播同一批动作, 按它去重)。</summary>
        public long Sn;

        /// <summary>店内卡牌配置 id。</summary>
        public int[] CardIds;

        /// <summary>本次欲购/已买的<b>候选槽位下标</b>(与 <see cref="CardIds"/> 对齐), 不是卡牌配置 id。</summary>
        public int[] BuyCards;

        /// <summary>"已买"标记(与 <see cref="CardIds"/> 对齐)。</summary>
        public bool[] AlreadyBought;

        /// <summary>天赋免费标记(与 <see cref="CardIds"/> 对齐)。只有 PVE 商店有, PVP 恒为空数组。</summary>
        public bool[] Free;

        /// <summary>报价总价(原价)。</summary>
        public int Gold;

        /// <summary>折扣后总价。PVP 商店没有该字段, 恒 0。</summary>
        public int DisCountGold;

        /// <summary>PVP 商店的免费卡配置 id(0 = 无)。PVE 用 <see cref="Free"/>。</summary>
        public int FreeCard;

        /// <summary>PVP 商店的免费张数。PVE 恒 0。</summary>
        public int FreeCardNum;

        /// <summary>PVE 转账目标(0 = 不是转账)。PVP 恒 0。</summary>
        public long AssistPlayer;

        /// <summary>PVE 转账金额。PVP 恒 0。</summary>
        public int AssistGold;

        /// <summary>是否离店(PVE 商店才有意义)。</summary>
        public bool IsClose;

        /// <summary>是否 PVE 商店报价(<c>false</c> = PVP)。</summary>
        public bool IsPve;

        /// <summary>逐张最终单价(与 <see cref="CardIds"/> 对齐); 未知为 -1。PVP 恒 -1(协议无逐张折扣/免费)。</summary>
        public int[] UnitPrices;

        /// <summary>
        /// 本次报价的应付款: 有折扣(<see cref="DisCountGold"/> &gt; 0)时取折扣价, 否则取原价。
        /// 每张卡各自的单价见 <see cref="UnitPrices"/>。
        /// </summary>
        public int TotalGold
        {
            get { return DisCountGold > 0 ? DisCountGold : Gold; }
        }

        public override string ToString()
        {
            return (IsPve ? "PVE" : "PVP") + " 商店报价(pid=" + PlayerId + ", sn=" + Sn +
                   ", 卡=" + (CardIds == null ? 0 : CardIds.Length) +
                   ", 总价=" + TotalGold + ")";
        }
    }
}
