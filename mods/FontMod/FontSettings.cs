using System;
using CesiumLoader.SDK.Configuration;

namespace FontMod
{
    /// <summary>
    /// FontMod v1.1.0 配置视图：100% 兼容 v1.0.0 与 Toys 字体页写入的字段，
    /// 并新增全量 TMP SDF 替换、旧版桥接 SDF、本地字体文件直读与字重/图集调优参数。
    /// </summary>
    internal sealed class FontSettings
    {
        internal const string DefaultFallbackFonts =
            "Microsoft YaHei,Microsoft YaHei UI,SimHei,SimSun,SimSun-ExtB,Yu Gothic,Meiryo,Microsoft JhengHei,Segoe UI Symbol";

        // v1.0.0 原有字段（Toys 字体页直接读写）
        internal bool Enabled;
        internal string FontFamily;
        internal bool ApplyToAllFonts;
        internal string FallbackFonts;
        internal bool CheckCoverage;

        // v1.1.0 新增字段（全面 TMP + 旧版 SDF 桥接 + 本地字库直读）
        internal string FontPath;
        internal string FontStyle;
        internal string TitleFontPath;
        internal bool ReplaceTmpFonts;
        internal bool BridgeDynamicToSdf;
        internal bool ReplaceSceneTmpText;
        internal int AtlasSize;
        internal int SamplingPointSize;
        internal int AtlasPadding;
        internal float TmpFontScale;
        internal float NormalWeight;
        internal float BoldWeight;
        internal bool WarmupGameCharset;

        internal bool HasTarget
        {
            get { return Enabled && (!string.IsNullOrEmpty(FontPath) || !string.IsNullOrEmpty(FontFamily)); }
        }

        internal static void EnsureDefaults(ModConfig config)
        {
            if (config == null) return;
            config.SetDefault("enabled", true);
            config.SetDefault("fontFamily", "");
            config.SetDefault("applyToAllFonts", true);
            config.SetDefault("fallbackFonts", DefaultFallbackFonts);
            config.SetDefault("checkCoverage", true);

            config.SetDefault("fontPath", "");
            config.SetDefault("fontStyle", "");
            config.SetDefault("titleFontPath", "");
            config.SetDefault("replaceTmpFonts", true);
            config.SetDefault("bridgeDynamicToSdf", true);
            config.SetDefault("replaceSceneTmpText", true);
            config.SetDefault("atlasSize", 4096);
            config.SetDefault("samplingPointSize", 48);
            config.SetDefault("atlasPadding", 5);
            config.SetDefault("tmpFontScale", 1.0);
            config.SetDefault("normalWeight", 0.0);
            config.SetDefault("boldWeight", 0.15);
            config.SetDefault("warmupGameCharset", true);
        }

        internal static FontSettings Read(ModConfig config)
        {
            var s = new FontSettings();
            s.Enabled = true;
            s.FontFamily = "";
            s.ApplyToAllFonts = true;
            s.FallbackFonts = DefaultFallbackFonts;
            s.CheckCoverage = true;

            s.FontPath = "";
            s.FontStyle = "";
            s.TitleFontPath = "";
            s.ReplaceTmpFonts = true;
            s.BridgeDynamicToSdf = true;
            s.ReplaceSceneTmpText = true;
            s.AtlasSize = 4096;
            s.SamplingPointSize = 48;
            s.AtlasPadding = 5;
            s.TmpFontScale = 1.0f;
            s.NormalWeight = 0.0f;
            s.BoldWeight = 0.15f;
            s.WarmupGameCharset = true;

            if (config == null) return s;

            s.Enabled = config.GetBool("enabled", true);
            s.FontFamily = TrimOrEmpty(config.GetString("fontFamily", ""));
            s.ApplyToAllFonts = config.GetBool("applyToAllFonts", true);
            s.FallbackFonts = TrimOrEmpty(config.GetString("fallbackFonts", DefaultFallbackFonts));
            s.CheckCoverage = config.GetBool("checkCoverage", true);

            s.FontPath = TrimOrEmpty(config.GetString("fontPath", ""));
            s.FontStyle = TrimOrEmpty(config.GetString("fontStyle", ""));
            s.TitleFontPath = TrimOrEmpty(config.GetString("titleFontPath", ""));
            s.ReplaceTmpFonts = config.GetBool("replaceTmpFonts", true);
            s.BridgeDynamicToSdf = config.GetBool("bridgeDynamicToSdf", true);
            s.ReplaceSceneTmpText = config.GetBool("replaceSceneTmpText", true);

            int atlas = config.GetInt("atlasSize", 4096);
            if (atlas < 1024) atlas = 1024;
            if (atlas > 8192) atlas = 8192;
            s.AtlasSize = atlas;

            int point = config.GetInt("samplingPointSize", 48);
            if (point < 16) point = 16;
            if (point > 96) point = 96;
            s.SamplingPointSize = point;

            int pad = config.GetInt("atlasPadding", 5);
            if (pad < 2) pad = 2;
            if (pad > 16) pad = 16;
            s.AtlasPadding = pad;

            float scale = (float)config.GetDouble("tmpFontScale", 1.0);
            if (scale < 0.5f || scale > 2.0f) scale = 1.0f;
            s.TmpFontScale = scale;

            float nw = (float)config.GetDouble("normalWeight", 0.0);
            if (nw < -0.5f) nw = -0.5f;
            if (nw > 0.5f) nw = 0.5f;
            s.NormalWeight = nw;

            float bw = (float)config.GetDouble("boldWeight", 0.15);
            if (bw < 0.0f) bw = 0.0f;
            if (bw > 1.0f) bw = 1.0f;
            s.BoldWeight = bw;

            s.WarmupGameCharset = config.GetBool("warmupGameCharset", true);
            return s;
        }

        internal bool SameAssetConfig(FontSettings other)
        {
            if (other == null) return false;
            return Enabled == other.Enabled
                && string.Equals(FontPath, other.FontPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(FontFamily, other.FontFamily, StringComparison.Ordinal)
                && string.Equals(FontStyle, other.FontStyle, StringComparison.OrdinalIgnoreCase)
                && string.Equals(TitleFontPath, other.TitleFontPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(FallbackFonts, other.FallbackFonts, StringComparison.Ordinal)
                && CheckCoverage == other.CheckCoverage
                && ReplaceTmpFonts == other.ReplaceTmpFonts
                && BridgeDynamicToSdf == other.BridgeDynamicToSdf
                && AtlasSize == other.AtlasSize
                && SamplingPointSize == other.SamplingPointSize
                && AtlasPadding == other.AtlasPadding
                && Math.Abs(TmpFontScale - other.TmpFontScale) < 0.0001f
                && Math.Abs(NormalWeight - other.NormalWeight) < 0.0001f
                && Math.Abs(BoldWeight - other.BoldWeight) < 0.0001f
                && WarmupGameCharset == other.WarmupGameCharset;
        }

        internal bool SameTarget(FontSettings other)
        {
            if (other == null) return false;
            return SameAssetConfig(other)
                && ApplyToAllFonts == other.ApplyToAllFonts
                && ReplaceSceneTmpText == other.ReplaceSceneTmpText;
        }

        internal string DescribeTarget()
        {
            if (!HasTarget) return "(未启用)";
            string main = !string.IsNullOrEmpty(FontPath) ? ("file=" + FontPath) : ("family=" + FontFamily);
            if (!string.IsNullOrEmpty(FontStyle)) main += " [" + FontStyle + "]";
            if (!string.IsNullOrEmpty(TitleFontPath)) main += ", titleFile=" + TitleFontPath;
            return main + " (all=" + ApplyToAllFonts + ", tmp=" + ReplaceTmpFonts + ", bridge=" + BridgeDynamicToSdf + ")";
        }

        private static string TrimOrEmpty(string value)
        {
            return value == null ? "" : value.Trim();
        }
    }
}
