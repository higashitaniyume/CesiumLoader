using CesiumLoader.SDK.Mods;

namespace StarCoinShopMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new StarCoinShopController());
        }
    }
}
