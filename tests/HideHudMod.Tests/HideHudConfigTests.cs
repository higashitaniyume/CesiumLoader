using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HideHudMod.Tests
{
    public sealed class HideHudConfigTests
    {
        [Fact]
        public void ConfigJson_ContainsHideTeammateIdsOnly_AndFullI18nComments()
        {
            // Find config.json path relative to test execution or project
            string configPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "mods", "HideHudMod", "config.json"));

            Assert.True(File.Exists(configPath), "config.json should exist at: " + configPath);

            string jsonText = File.ReadAllText(configPath);
            var obj = JObject.Parse(jsonText);

            Assert.True(obj.ContainsKey("hideTeammateIdsOnly"), "config.json must contain hideTeammateIdsOnly");
            Assert.False((bool)obj["hideTeammateIdsOnly"], "hideTeammateIdsOnly should be false by default");

            var comments = obj["_comments"] as JObject;
            Assert.NotNull(comments);
            Assert.True(comments.ContainsKey("hideTeammateIdsOnly"), "_comments must describe hideTeammateIdsOnly");

            string[] requiredLangs = { "zh-Hans", "zh-Hant", "en", "ja" };
            foreach (var lang in requiredLangs)
            {
                var doc = comments["hideTeammateIdsOnly"]?[lang]?.ToString();
                Assert.False(string.IsNullOrWhiteSpace(doc), $"_comments.hideTeammateIdsOnly must contain non-empty {lang}");
            }
        }
    }
}
