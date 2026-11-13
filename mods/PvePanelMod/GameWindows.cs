using System.Reflection;
using CesiumLoader.SDK.Gameplay;
using FairyGUI;
using GameLogic;
using Tools;
using UI;

namespace PvePanelMod
{
    /// <summary>
    /// 官方的商店窗口、战斗（投牌）窗口现在是不是开着。左栏会挡住它们，开着时自动收起左栏。
    /// 读 UIManager 里已经建好的窗口字段（照 CombatOddsMod 读 _cachedFightWindow 的做法），
    /// 不用 UIManager.Fight / landShop 属性：属性在窗口还没建时可能顺手把它建出来。读不到一律当作「没开」。
    /// </summary>
    internal static class GameWindows
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo FightField = typeof(UIManager).GetField("_cachedFightWindow", Private);
        private static readonly FieldInfo ShopField = typeof(UIManager).GetField("_cachedLandShopWindow", Private);
        // 转账窗口：从商店里点转账时，商店窗口先关、这个窗口再开；只有自己转账时才会出现
        private static readonly FieldInfo AtmField = typeof(UIManager).GetField("_cachedATMWindow", Private);
        // 商店窗口自己记着正在逛的人
        private static readonly FieldInfo ShopperField = typeof(LandShopWindow).GetField("shoppingPlayer", Private | BindingFlags.Public);

        /// <summary>转账窗口开着（只有自己能打开）。</summary>
        public static bool AtmOpen() { return Showing(AtmField); }

        /// <summary>
        /// 自己的战斗窗口开着。别人打架时这个窗口也会弹出来（观战），所以还要看自己是不是攻方或守方
        /// （照 CombatOddsMod 读 GameLogicManager.fight.battleFightData 的做法）。
        /// </summary>
        public static bool FightOpen()
        {
            if (!Showing(FightField)) return false;
            try
            {
                var live = SimpleSingletonProvider<GameLogicManager>.inst?.fight?.battleFightData;
                if (live == null || live.isEnd) return false;
                return (live.attackerInfo != null && Players.IsSelf(live.attackerInfo.PlayerId))
                    || (live.defenderInfo != null && Players.IsSelf(live.defenderInfo.PlayerId));
            }
            catch { return false; }
        }

        /// <summary>
        /// 自己的商店开着。别人进店时这个窗口也会弹出来，所以还要看在逛的是不是自己：
        /// 先看商店窗口自己的 shoppingPlayer；读不到再看右栏收到的最近一次进店消息（<paramref name="lastShopper"/>，商店一回合只有一个人在逛）。
        /// </summary>
        public static bool ShopOpen(long lastShopper)
        {
            if (!Showing(ShopField)) return false;
            long shopper = 0;
            try
            {
                var window = ShopField.GetValue(UIManager.inst);
                var pd = window != null && ShopperField != null ? ShopperField.GetValue(window) as BattlePlayerData : null;
                if (pd?.player != null) shopper = pd.player.Id;
            }
            catch { }
            if (shopper == 0) shopper = lastShopper;
            return shopper != 0 && Players.IsSelf(shopper);
        }

        private static bool Showing(FieldInfo field)
        {
            try
            {
                var ui = UIManager.inst;
                if (ui == null || field == null) return false;
                var window = field.GetValue(ui) as Window;
                return window != null && !window.isDisposed && window.isShowing;
            }
            catch { return false; }
        }
    }
}
