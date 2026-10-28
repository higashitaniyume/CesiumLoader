using System;
using System.IO;
using System.Text.Json;
using CesiumLoader.SDK.Configuration;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>config.json 的 _comments 保留键: 写入 / 合并 / 保留 / 与业务键隔离。</summary>
    public class ConfigDocTests
    {
        private static ModConfig NewConfig(out string dir, string modId)
        {
            dir = TestEnv.NewTempDir("configdoc");
            ModConfig.ClearCache();
            return ModConfig.ForModId(modId, dir);
        }

        private static JsonDocument ReadJson(ModConfig cfg)
        {
            return JsonDocument.Parse(File.ReadAllText(cfg.Path));
        }

        [Fact]
        public void Describe_WritesFourLanguagesAndKeepsCommentsLast()
        {
            string dir;
            var cfg = NewConfig(out dir, "DocMod");
            cfg.Set("cooldownSeconds", 2);
            Assert.True(cfg.Describe("cooldownSeconds", ConfigDoc.Of(
                "冷却秒数", "冷卻秒數", "Cooldown in seconds", "クールダウン（秒）")));
            Assert.True(cfg.Save());

            string text = File.ReadAllText(cfg.Path);

            using var doc = JsonDocument.Parse(text);
            // 说明放在文件末尾: 最后一个顶层属性必须是 _comments(按文档顺序枚举)。
            string lastProperty = "";
            foreach (var property in doc.RootElement.EnumerateObject()) lastProperty = property.Name;
            Assert.Equal(ModConfig.CommentsKey, lastProperty);

            var comments = doc.RootElement.GetProperty(ModConfig.CommentsKey);
            Assert.Equal("冷却秒数", comments.GetProperty("cooldownSeconds").GetProperty("zh-Hans").GetString());
            Assert.Equal("冷卻秒數", comments.GetProperty("cooldownSeconds").GetProperty("zh-Hant").GetString());
            Assert.Equal("Cooldown in seconds", comments.GetProperty("cooldownSeconds").GetProperty("en").GetString());
            Assert.Equal("クールダウン（秒）", comments.GetProperty("cooldownSeconds").GetProperty("ja").GetString());
        }

        [Fact]
        public void SetDefault_WithDoc_AddsKeyAndComment()
        {
            string dir;
            var cfg = NewConfig(out dir, "DefaultDocMod");

            Assert.True(cfg.SetDefault("enabled", true, ConfigDoc.All("Enable this module")));
            Assert.True(cfg.GetBool("enabled"));
            Assert.True(cfg.Save());

            using var doc = ReadJson(cfg);
            Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
            var comment = doc.RootElement.GetProperty(ModConfig.CommentsKey).GetProperty("enabled");
            Assert.Equal("Enable this module", comment.GetProperty("zh-Hans").GetString());
            Assert.Equal("Enable this module", comment.GetProperty("zh-Hant").GetString());
            Assert.Equal("Enable this module", comment.GetProperty("en").GetString());
            Assert.Equal("Enable this module", comment.GetProperty("ja").GetString());
        }

        [Fact]
        public void SetDefault_WithDoc_DoesNotOverwriteUserValue()
        {
            string dir;
            var cfg = NewConfig(out dir, "KeepValueMod");
            cfg.Set("cooldownSeconds", 7);

            Assert.True(cfg.SetDefault("cooldownSeconds", 2, ConfigDoc.All("冷却")));
            Assert.Equal(7, cfg.GetInt("cooldownSeconds"));
            Assert.True(cfg.Save());

            using var doc = ReadJson(cfg);
            Assert.Equal(7, doc.RootElement.GetProperty("cooldownSeconds").GetInt32());
            Assert.True(doc.RootElement.GetProperty(ModConfig.CommentsKey).TryGetProperty("cooldownSeconds", out _));
        }

        [Fact]
        public void Describe_PreservesOtherLanguagesAndStringShorthand()
        {
            string dir;
            var cfg = NewConfig(out dir, "MergeDocMod");
            File.WriteAllText(cfg.Path,
                "{\"speed\":2,\"_comments\":{\"speed\":\"共用說明\",\"other\":{\"ja\":\"既存\"}}}");
            Assert.True(cfg.Reload());

            Assert.True(cfg.Describe("speed", ConfigDoc.Of("中文说明", null, "English text", null)));
            Assert.True(cfg.Save());

            using var doc = ReadJson(cfg);
            var comments = doc.RootElement.GetProperty(ModConfig.CommentsKey);
            var speed = comments.GetProperty("speed");
            Assert.Equal("中文说明", speed.GetProperty("zh-Hans").GetString());
            Assert.Equal("共用說明", speed.GetProperty("zh-Hant").GetString());
            Assert.Equal("English text", speed.GetProperty("en").GetString());
            Assert.Equal("共用說明", speed.GetProperty("ja").GetString());
            Assert.Equal("既存", comments.GetProperty("other").GetProperty("ja").GetString());
        }

        [Fact]
        public void Describe_SameContentTwice_DoesNotMarkDirty()
        {
            string dir;
            var cfg = NewConfig(out dir, "IdempotentDocMod");
            var doc = ConfigDoc.All("说明");

            Assert.True(cfg.Describe("x", doc));
            Assert.True(cfg.Save());
            Assert.False(cfg.IsDirty);

            Assert.False(cfg.Describe("x", ConfigDoc.All("说明")));
            Assert.False(cfg.IsDirty);
        }

        [Fact]
        public void UnrelatedSave_KeepsExistingComments()
        {
            string dir;
            var cfg = NewConfig(out dir, "KeepDocMod");
            cfg.Set("x", 1);
            cfg.Describe("x", ConfigDoc.All("说明"));
            cfg.Save();

            cfg.Set("y", 2);
            Assert.True(cfg.Save());

            using var doc = ReadJson(cfg);
            Assert.Equal(1, doc.RootElement.GetProperty("x").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("y").GetInt32());
            Assert.Equal("说明", doc.RootElement.GetProperty(ModConfig.CommentsKey)
                .GetProperty("x").GetProperty("zh-Hans").GetString());
        }

        [Fact]
        public void AllAndCount_ExcludeComments()
        {
            string dir;
            var cfg = NewConfig(out dir, "CountDocMod");
            cfg.Set("a", 1);
            cfg.Set("b", 2);
            cfg.Describe("a", ConfigDoc.All("A"));

            Assert.Equal(2, cfg.Count);
            Assert.Equal(2, cfg.All.Count);
            Assert.False(cfg.All.ContainsKey(ModConfig.CommentsKey));
            Assert.True(cfg.Has(ModConfig.CommentsKey));
            Assert.Equal(1, cfg.GetInt("a"));
            Assert.Equal(2, cfg.GetInt("b"));
        }
    }
}
