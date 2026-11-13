using System;
using CesiumLoader.SDK.Configuration;

namespace PinnedRelicsMod
{
    internal sealed class Settings
    {
        internal readonly bool Enabled;
        internal readonly float Size, Gap, X, Y;
        internal readonly RelicIcon[] Selection;
        internal readonly string[] UnknownNames;

        private Settings(bool enabled, float size, float gap, float x, float y, string names)
        {
            Enabled = enabled; Size = size; Gap = gap; X = x; Y = y;
            Selection = RelicCatalog.Select(names, out var unknown);
            UnknownNames = unknown;
        }

        internal static Settings Load(ModConfig config)
        {
            return new Settings(config.GetBool("Enabled", true),
                Read(config, "IconSize", 30, 16, 64), Read(config, "IconGap", 3, 0, 20),
                Read(config, "OffsetX", 0, -300, 300), Read(config, "OffsetY", 3, -100, 200),
                config.GetString("PinnedRelicNames", RelicCatalog.DefaultNames));
        }

        private static float Read(ModConfig config, string key, double fallback, double min, double max)
        {
            double value = config.GetDouble(key, fallback);
            return (float)(double.IsNaN(value) || double.IsInfinity(value)
                ? fallback : Math.Max(min, Math.Min(max, value)));
        }

        internal static void Defaults(ModConfig config)
        {
            config.SetDefault("Enabled", true);
            config.SetDefault("IconSize", 30);
            config.SetDefault("IconGap", 3);
            config.SetDefault("OffsetX", 0);
            config.SetDefault("OffsetY", 3);
            config.SetDefault("PinnedRelicNames", RelicCatalog.DefaultNames);
            config.Describe("PinnedRelicNames", ConfigDoc.Of(
                "置顶筹码中文名，按输入顺序显示，用逗号、顿号、分号或换行分隔。追加名字可新增，删掉名字可取消；留空全部不显示。仅显示玩家已持有的筹码。完整名称见随包《支持的筹码.md》。",
                "置頂籌碼中文名，依輸入順序顯示，以逗號、頓號、分號或換行分隔。可新增、刪除；留空不顯示。僅顯示已持有的籌碼。",
                "Pinned relic Chinese names in display order, separated by commas, semicolons or newlines. Empty hides all. Only owned relics appear. See the supported names file.",
                "中国語のチップ名を表示順に入力し、カンマや改行で区切ります。空欄で非表示。所持中のみ表示。"));
            config.Describe("Enabled", ConfigDoc.Of("总开关。仅在玩家持有指定筹码时显示图标。",
                "總開關。僅在玩家持有指定籌碼時顯示圖示。", "Show icons only for selected relics currently owned.", "対象チップを所持している間だけアイコンを表示。"));
            config.Describe("IconSize", ConfigDoc.Of("图标边长，默认 30，范围 16～64；一行空间不足时自动缩小。",
                "圖示邊長，預設 30，範圍 16～64；一行空間不足時自動縮小。", "Icon size, 16–64. Shrinks to keep all icons on one row.", "アイコンサイズ（16～64）。一列に収まるよう自動縮小。"));
            config.Describe("IconGap", ConfigDoc.Of("图标间距，默认 3，范围 0～20。",
                "圖示間距，預設 3，範圍 0～20。", "Spacing between icons, 0–20.", "アイコン間隔（0～20）。"));
            config.Describe("OffsetX", ConfigDoc.Of("相对星币数字左边缘的水平偏移，正数向右。",
                "相對星幣數字左緣的水平偏移，正數向右。", "Horizontal offset from the coin number; positive moves right.", "星コイン数字からの横位置。正数は右。"));
            config.Describe("OffsetY", ConfigDoc.Of("相对星币数字下边缘的垂直偏移，默认 3，正数向下。",
                "相對星幣數字下緣的垂直偏移，預設 3，正數向下。", "Vertical gap below the coin number; positive moves down.", "星コイン数字の下端からの縦位置。正数は下。"));
            config.Save();
        }
    }
}
