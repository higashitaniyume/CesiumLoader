using CesiumLoader.SDK.Mods;

namespace LanguageUnlockMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new LanguageController());
        }
    }
}
