using System;
using CesiumLoader.SDK.Configuration;

namespace FontMod
{
    /// <summary>
    /// <c>config.json</c> 的强类型视图。缺省值写在 <see cref="EnsureDefaults"/> 里，
    /// 读取失败/文件损坏时退回默认值继续跑，不把异常抛给游戏。
    /// </summary>
    internal sealed class FontSettings
    {
        internal const string DefaultFallbackFonts =
            "Microsoft YaHei,Microsoft YaHei UI,SimHei,SimSun,SimSun-ExtB,Yu Gothic,Meiryo,Microsoft JhengHei,Segoe UI Symbol";

        internal bool Enabled;
        internal string FontFamily;
        internal bool ApplyToAllFonts;
        internal string FallbackFonts;
        internal bool CheckCoverage;

        internal static void EnsureDefaults(ModConfig config)
        {
            if (config == null) return;
            config.SetDefault("enabled", true);
            config.SetDefault("fontFamily", "");
            config.SetDefault("applyToAllFonts", true);
            config.SetDefault("fallbackFonts", DefaultFallbackFonts);
            config.SetDefault("checkCoverage", true);
        }

        internal static FontSettings Read(ModConfig config)
        {
            var settings = new FontSettings();
            settings.Enabled = true;
            settings.FontFamily = "";
            settings.ApplyToAllFonts = true;
            settings.FallbackFonts = DefaultFallbackFonts;
            settings.CheckCoverage = true;
            if (config == null) return settings;

            settings.Enabled = config.GetBool("enabled", true);
            string family = config.GetString("fontFamily", "");
            settings.FontFamily = family == null ? "" : family.Trim();
            settings.ApplyToAllFonts = config.GetBool("applyToAllFonts", true);
            string fallback = config.GetString("fallbackFonts", DefaultFallbackFonts);
            settings.FallbackFonts = fallback == null ? "" : fallback.Trim();
            settings.CheckCoverage = config.GetBool("checkCoverage", true);
            return settings;
        }

        internal bool SameTarget(FontSettings other)
        {
            if (other == null) return false;
            return Enabled == other.Enabled
                && string.Equals(FontFamily, other.FontFamily, StringComparison.Ordinal)
                && ApplyToAllFonts == other.ApplyToAllFonts
                && string.Equals(FallbackFonts, other.FallbackFonts, StringComparison.Ordinal)
                && CheckCoverage == other.CheckCoverage;
        }
    }
}
