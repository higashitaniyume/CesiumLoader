using System;
using System.IO;
using CesiumLoader.SDK.Configuration;
using FontMod;
using Xunit;

namespace FontMod.Tests
{
    public sealed class FontFileResolverAndSettingsTests
    {
        [Fact]
        public void NormalizeName_StripsSpacesHyphensUnderscoresAndLowercases()
        {
            Assert.Equal("microsoftyaheiui", FontFileResolver.NormalizeName("Microsoft YaHei UI"));
            Assert.Equal("fzxiaozhuantis13", FontFileResolver.NormalizeName("FZXiaoZhuanTi-S13"));
            Assert.Equal("方正小篆体gbk", FontFileResolver.NormalizeName("方正小篆体_GBK"));
            Assert.Equal("", FontFileResolver.NormalizeName(null));
            Assert.Equal("", FontFileResolver.NormalizeName("   "));
        }

        [Fact]
        public void Resolve_ExplicitFontPath_FindsAbsoluteAndRelativeFiles()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FontModTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string fakeFont = Path.Combine(tempDir, "demo.ttf");
                File.WriteAllBytes(fakeFont, new byte[] { 0, 1, 0, 0 });

                ResolvedFontFile abs = FontFileResolver.Resolve(fakeFont, "", "", "", "", null, null, null);
                Assert.NotNull(abs);
                Assert.Equal(Path.GetFullPath(fakeFont), abs.FilePath);
                Assert.Equal(0, abs.FaceIndex);

                ResolvedFontFile rel = FontFileResolver.Resolve("demo.ttf", "", "", tempDir, "", null, null, null);
                Assert.NotNull(rel);
                Assert.Equal(Path.GetFullPath(fakeFont), rel.FilePath);

                ResolvedFontFile missing = FontFileResolver.Resolve(
                    Path.Combine(tempDir, "does-not-exist.ttf"), "", "", tempDir, "", null, null, null);
                Assert.Null(missing);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void FontSettings_ReadNull_ReturnsExpectedV11Defaults()
        {
            FontSettings s = FontSettings.Read(null);

            Assert.True(s.Enabled);
            Assert.Equal("", s.FontFamily);
            Assert.Equal("", s.FontPath);
            Assert.True(s.ApplyToAllFonts);
            Assert.True(s.ReplaceTmpFonts);
            Assert.True(s.BridgeDynamicToSdf);
            Assert.True(s.ReplaceSceneTmpText);
            Assert.Equal(4096, s.AtlasSize);
            Assert.Equal(48, s.SamplingPointSize);
            Assert.Equal(5, s.AtlasPadding);
            Assert.Equal(1.0f, s.TmpFontScale);
            Assert.Equal(0.0f, s.NormalWeight);
            Assert.Equal(0.15f, s.BoldWeight);
            Assert.True(s.WarmupGameCharset);
            Assert.False(s.HasTarget);
        }

        [Fact]
        public void FontSettings_HasTargetAndComparison_RespectsFontPathAndSdfParams()
        {
            FontSettings a = FontSettings.Read(null);
            FontSettings b = FontSettings.Read(null);

            Assert.True(a.SameTarget(b));

            a.FontPath = @"C:\Fonts\Test.ttf";
            Assert.True(a.HasTarget);
            Assert.False(a.SameAssetConfig(b));

            b.FontPath = @"C:\Fonts\Test.ttf";
            Assert.True(a.SameAssetConfig(b));

            b.BoldWeight = 0.0f;
            Assert.False(a.SameAssetConfig(b));

            b.BoldWeight = a.BoldWeight;
            b.ApplyToAllFonts = false;
            Assert.True(a.SameAssetConfig(b));
            Assert.False(a.SameTarget(b));
        }

        [Fact]
        public void FontSettings_ClampsOutOfRangeValuesFromConfig()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FontModCfg_" + Guid.NewGuid().ToString("N"));
            string prevLoaderDir = Environment.GetEnvironmentVariable("CESIUM_LOADER_DIR");
            try
            {
                Environment.SetEnvironmentVariable("CESIUM_LOADER_DIR", tempDir);
                ModConfig.ClearCache();

                string modId = "FontModTest_" + Guid.NewGuid().ToString("N");
                // 4.0.0 起配置只在模组目录里：<mods>\<ModId>\config.json。
                string modDir = Path.Combine(tempDir, "mods", modId);
                Directory.CreateDirectory(modDir);
                string cfgFile = Path.Combine(modDir, "config.json");
                File.WriteAllText(cfgFile,
                    "{\"atlasSize\": 128, \"samplingPointSize\": 999, \"atlasPadding\": 99, \"tmpFontScale\": 5.0, \"normalWeight\": -2.0, \"boldWeight\": 3.0}");

                ModConfig cfg = ModConfig.ForModId(modId, modDir);
                FontSettings s = FontSettings.Read(cfg);

                Assert.Equal(1024, s.AtlasSize);
                Assert.Equal(96, s.SamplingPointSize);
                Assert.Equal(16, s.AtlasPadding);
                Assert.Equal(1.0f, s.TmpFontScale);
                Assert.Equal(-0.5f, s.NormalWeight);
                Assert.Equal(1.0f, s.BoldWeight);
            }
            finally
            {
                ModConfig.ClearCache();
                Environment.SetEnvironmentVariable("CESIUM_LOADER_DIR", prevLoaderDir);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
    }
}
