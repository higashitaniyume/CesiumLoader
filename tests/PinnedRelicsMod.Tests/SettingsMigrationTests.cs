using System;
using System.IO;
using CesiumLoader.SDK.Configuration;
using PinnedRelicsMod;
using Xunit;

namespace PinnedRelicsMod.Tests
{
    /// <summary>
    /// 1.2.0 shipped PascalCase config keys. Upgrading must carry user values over
    /// instead of silently rewriting the list back to the defaults.
    /// </summary>
    public sealed class SettingsMigrationTests
    {
        private static ModConfig CreateConfig(out string directory)
        {
            directory = Path.Combine(Path.GetTempPath(), "PinnedRelicsMod.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            // 配置写在 <加载器目录>/configs 下：指到本用例的临时目录，免得写到全局目录里。
            Environment.SetEnvironmentVariable("CESIUM_LOADER_DIR", directory);
            Directory.CreateDirectory(Path.Combine(directory, "configs"));
            // Unique ModId: ModConfig caches instances by id for the process lifetime.
            return ModConfig.ForModId("PinnedRelicsMod.Tests." + Guid.NewGuid().ToString("N"), directory);
        }

        [Fact]
        public void LegacyKeysAreMigratedWithoutLosingUserValues()
        {
            string directory;
            var config = CreateConfig(out directory);
            try
            {
                config.Set("Enabled", false);
                config.Set("IconSize", 44);
                config.Set("IconGap", 7);
                config.Set("OffsetX", -12);
                config.Set("OffsetY", 9);
                config.Set("PinnedRelicNames", "星币锤,紫色飞星");

                Settings.Defaults(config);

                Assert.True(config.GetBool("enabled", true) == false, "enabled was not migrated");
                Assert.True(Math.Abs(config.GetDouble("iconSize", 0) - 44) < 0.001, "iconSize was not migrated");
                Assert.True(Math.Abs(config.GetDouble("iconGap", 0) - 7) < 0.001, "iconGap was not migrated");
                Assert.True(Math.Abs(config.GetDouble("offsetX", 0) + 12) < 0.001, "offsetX was not migrated");
                Assert.True(Math.Abs(config.GetDouble("offsetY", 0) - 9) < 0.001, "offsetY was not migrated");
                Assert.True(config.GetString("pinnedRelicNames", null) == "星币锤,紫色飞星", "pinnedRelicNames was not migrated");

                foreach (var legacy in new[] { "Enabled", "IconSize", "IconGap", "OffsetX", "OffsetY", "PinnedRelicNames" })
                    Assert.False(config.Has(legacy), legacy + " was left behind after migration");

                var settings = Settings.Load(config);
                Assert.False(settings.Enabled, "enabled reached Settings.Load wrongly");
                Assert.Equal(44f, settings.Size);
                Assert.Equal(7f, settings.Gap);
                Assert.Equal(-12f, settings.X);
                Assert.Equal(9f, settings.Y);
                Assert.True(settings.Selection.Length == 2 && settings.UnknownNames.Length == 0,
                    "migrated relic list did not resolve");
            }
            finally
            {
                ModConfig.ClearCache();
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        [Fact]
        public void FreshConfigGetsDefaults()
        {
            string directory;
            var config = CreateConfig(out directory);
            try
            {
                Settings.Defaults(config);

                var settings = Settings.Load(config);
                Assert.True(settings.Enabled);
                Assert.Equal(30f, settings.Size);
                Assert.Equal(3f, settings.Gap);
                Assert.Equal(0f, settings.X);
                Assert.Equal(3f, settings.Y);
                Assert.True(settings.Selection.Length == 8, "default list did not resolve to eight relics");
                // 统一布局：配置写进 <加载器目录>/configs/<ModId>.json，不再放模组目录。
                Assert.True(config.Path != null && File.Exists(config.Path), "Defaults did not persist config.json");
            }
            finally
            {
                ModConfig.ClearCache();
                try { Directory.Delete(directory, true); } catch { }
            }
        }
    }
}
