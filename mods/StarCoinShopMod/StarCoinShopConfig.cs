using System;
using UnityEngine;

namespace StarCoinShopMod
{
    public sealed class StarCoinShopConfig
    {
        public bool Enabled = true;
        public float CooldownSeconds = 0.8f;
        public string QuickBuyKey = "F";
        public bool ShowButton = true;
        public float ButtonOffsetX = 0f;
        public bool NotifyOnComplete = true;

        // 默认自动购买 1, 2, 7, 8 格商品（1、2 为每日免费道具，7、8 为 300 星币徽章）
        public bool BuySlot1 = true;
        public bool BuySlot2 = true;
        public bool BuySlot3 = false;
        public bool BuySlot4 = false;
        public bool BuySlot5 = false;
        public bool BuySlot6 = false;
        public bool BuySlot7 = true;
        public bool BuySlot8 = true;

        public bool IsSlotEnabled(int slotIndex)
        {
            switch (slotIndex)
            {
                case 1: return BuySlot1;
                case 2: return BuySlot2;
                case 3: return BuySlot3;
                case 4: return BuySlot4;
                case 5: return BuySlot5;
                case 6: return BuySlot6;
                case 7: return BuySlot7;
                case 8: return BuySlot8;
                default: return false;
            }
        }

        public KeyCode ParsedQuickBuyKey
        {
            get
            {
                if (string.IsNullOrEmpty(QuickBuyKey)) return KeyCode.F;
                try
                {
                    KeyCode k;
                    return Enum.TryParse(QuickBuyKey, true, out k) ? k : KeyCode.F;
                }
                catch
                {
                    return KeyCode.F;
                }
            }
        }
    }
}
