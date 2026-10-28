using CesiumLoader.SDK.Configuration;

namespace CharacterSkinMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class CharacterSkinConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("Enabled", D(
                "总开关：关闭后不加载任何角色皮肤。",
                "總開關：關閉後不載入任何角色面板。",
                "Master switch: no character skins are loaded when off.",
                "全体スイッチ：オフにするとキャラクタースキンを読み込みません。"));
            changed |= config.Describe("ActiveSchemeId", D(
                "当前启用的角色皮肤方案 ID；留空时使用下方的旧版包列表。",
                "目前啟用的角色面板方案 ID；留空時使用下方的舊版包清單。",
                "Active character skin scheme ID; leave empty to use the legacy package list below.",
                "有効なキャラクタースキン方案 ID。空欄の場合は下の旧版パッケージ一覧を使います。"));
            changed |= config.Describe("ActivePackages", D(
                "旧版启用的皮肤包 ID 列表；设置 ActiveSchemeId 后不再使用。",
                "舊版啟用的面板包 ID 清單；設定 ActiveSchemeId 後不再使用。",
                "Legacy list of active skin package IDs; ignored when ActiveSchemeId is set.",
                "旧版の有効なスキンパッケージ ID 一覧。ActiveSchemeId を設定すると使われません。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
