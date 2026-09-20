using System.IO;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>每个 mod 独立的 config.json: 类型支持 / 落盘 / 损坏文件降级。</summary>
    public class ModConfigTests
    {
        private enum Quality { Low = 0, Medium = 1, High = 2 }

        private sealed class Nested
        {
            public int Level = 7;
            public string Label = "nested";
        }

        private static ModConfig NewConfig(out string dir, string modId)
        {
            dir = TestEnv.NewTempDir("config");
            ModConfig.ClearCache();
            return ModConfig.ForModId(modId, dir);
        }

        [Fact]
        public void ForModId_ResolvesConfigInsideModDirectory()
        {
            string dir;
            var cfg = NewConfig(out dir, "ConfigPathMod");

            Assert.NotNull(cfg.Path);
            Assert.Equal(Path.Combine(dir, "config.json"), cfg.Path);
            Assert.Equal("ConfigPathMod", cfg.ModId);
        }

        [Fact]
        public void MissingFile_LoadsEmptyWithDefaults()
        {
            string dir;
            var cfg = NewConfig(out dir, "MissingFileMod");

            Assert.False(File.Exists(cfg.Path));
            Assert.False(cfg.Has("speed"));
            Assert.Equal(0, cfg.Count);
            Assert.Equal(5.5f, cfg.GetFloat("speed", 5.5f));
            Assert.Equal("fallback", cfg.GetString("name", "fallback"));
        }

        [Fact]
        public void SetAndGet_RoundTripsScalarTypes()
        {
            string dir;
            var cfg = NewConfig(out dir, "ScalarMod");

            cfg.Set("speed", 5.0f);
            cfg.Set("count", 3);
            cfg.Set("enabled", true);
            cfg.Set("label", "hello");
            cfg.Set("ratio", 0.25d);
            cfg.Set("quality", Quality.High);

            Assert.Equal(5.0f, cfg.GetFloat("speed"));
            Assert.Equal(3, cfg.GetInt("count"));
            Assert.True(cfg.GetBool("enabled"));
            Assert.Equal("hello", cfg.GetString("label"));
            Assert.Equal(0.25d, cfg.GetDouble("ratio"));
            Assert.Equal(Quality.High, cfg.GetEnum("quality", Quality.Low));
        }

        [Fact]
        public void Get_TypeMismatchOrMissing_ReturnsDefault()
        {
            string dir;
            var cfg = NewConfig(out dir, "MismatchMod");

            cfg.Set("label", "not a number");

            Assert.Equal(42, cfg.GetInt("label", 42));
            Assert.Equal(1.5f, cfg.GetFloat("nope", 1.5f));
            Assert.Equal(Quality.Medium, cfg.GetEnum("nope", Quality.Medium));
            Assert.False(cfg.TryGet<int>("nope", out _));
        }

        [Fact]
        public void TryGet_ReportsPresence()
        {
            string dir;
            var cfg = NewConfig(out dir, "TryGetMod");
            cfg.Set("fov", 60);

            int value;
            Assert.True(cfg.TryGet("fov", out value));
            Assert.Equal(60, value);
        }

        [Fact]
        public void NestedObject_IsSerializedAndRestored()
        {
            string dir;
            var cfg = NewConfig(out dir, "NestedMod");

            cfg.Set("inner", new Nested { Level = 9, Label = "deep" });
            Assert.True(cfg.Save());

            var reloaded = ModConfig.ForModId("NestedMod", dir);
            Assert.True(reloaded.Reload());

            var inner = reloaded.Get<Nested>("inner");
            Assert.NotNull(inner);
            Assert.Equal(9, inner.Level);
            Assert.Equal("deep", inner.Label);
        }

        [Fact]
        public void Set_MarksDirty_SaveClearsIt()
        {
            string dir;
            var cfg = NewConfig(out dir, "DirtyMod");

            Assert.False(cfg.IsDirty);
            cfg.Set("speed", 10f);
            Assert.True(cfg.IsDirty);

            Assert.True(cfg.Save());
            Assert.False(cfg.IsDirty);
            Assert.True(cfg.SaveIfDirty());    // 已干净 → 视为成功, 不重复写
        }

        [Fact]
        public void Save_ThenReload_PersistsValues()
        {
            string dir;
            var cfg = NewConfig(out dir, "PersistMod");

            cfg.Set("speed", 12.5f);
            cfg.Set("toggleKey", "F1");
            cfg.Set("invertY", true);
            Assert.True(cfg.Save());

            Assert.True(File.Exists(cfg.Path));
            string text = File.ReadAllText(cfg.Path);
            Assert.Contains("\n", text);         // 美化输出, 便于人工编辑
            Assert.Contains("speed", text);

            // 模拟重启: 清缓存后重新加载
            ModConfig.ClearCache();
            var again = ModConfig.ForModId("PersistMod", dir);
            Assert.Equal(12.5f, again.GetFloat("speed"));
            Assert.Equal("F1", again.GetString("toggleKey"));
            Assert.True(again.GetBool("invertY"));
        }

        [Fact]
        public void ModifyThenReload_DiscardsUnsavedChanges()
        {
            string dir;
            var cfg = NewConfig(out dir, "ReloadMod");
            cfg.Set("speed", 1f);
            cfg.Save();

            cfg.Set("speed", 99f);
            Assert.True(cfg.Reload());

            Assert.Equal(1f, cfg.GetFloat("speed"));
            Assert.False(cfg.IsDirty);
        }

        [Fact]
        public void RemoveAndClear_UpdateCount()
        {
            string dir;
            var cfg = NewConfig(out dir, "RemoveMod");
            cfg.Set("a", 1);
            cfg.Set("b", 2);
            Assert.Equal(2, cfg.Count);

            Assert.True(cfg.Remove("a"));
            Assert.False(cfg.Remove("a"));
            Assert.Equal(1, cfg.Count);

            cfg.Clear();
            Assert.Equal(0, cfg.Count);
            Assert.True(cfg.IsDirty);
        }

        [Fact]
        public void CorruptFile_IsBackedUpAndLoadsEmpty()
        {
            string dir;
            var cfg = NewConfig(out dir, "CorruptMod");
            File.WriteAllText(cfg.Path, "{ this is not json");

            Assert.False(cfg.Reload());
            Assert.Equal(0, cfg.Count);
            Assert.True(File.Exists(cfg.Path + ".bak"), "损坏的配置应被备份为 .bak");
        }

        [Fact]
        public void NonObjectTopLevel_IsIgnoredWithoutThrowing()
        {
            string dir;
            var cfg = NewConfig(out dir, "ArrayMod");
            File.WriteAllText(cfg.Path, "[1,2,3]");

            Assert.False(cfg.Reload());
            Assert.Equal(0, cfg.Count);
        }

        [Fact]
        public void All_ReturnsSnapshotNotLiveData()
        {
            string dir;
            var cfg = NewConfig(out dir, "SnapshotMod");
            cfg.Set("speed", 1f);

            var snapshot = cfg.All;
            cfg.Set("speed", 2f);

            Assert.Equal(1f, (float)snapshot["speed"]);
        }

        [Fact]
        public void EmptyKeyAndNullValues_DoNotThrow()
        {
            string dir;
            var cfg = NewConfig(out dir, "EdgeMod");

            cfg.Set<string>(null, "x");
            cfg.Set("nullValue", (string)null);

            Assert.False(cfg.Has(null));       // 空 key 被忽略
            Assert.Equal(1, cfg.Count);        // 只有 nullValue 生效
            Assert.True(cfg.Has("nullValue"));
            Assert.Null(cfg.GetString("nullValue"));
        }
    }
}
