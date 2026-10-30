using System.Collections.Generic;
using Google.Protobuf.Collections;
using Xunit;

namespace LanguageUnlockMod.Tests
{
    /// <summary>
    /// 用真实 protobuf 类型跑一遍 STR 合并: 覆盖 private set 的反射写入、缺失条目回退简中、
    /// 以及只改目标语言那一列。
    /// </summary>
    public class StrTableMergeTests
    {
        private static STRSkillLocalConfigure Entry(int id, string simplified, string english = "", string japanese = "", string traditional = "")
        {
            var entry = new STRSkillLocalConfigure();
            Assert.True(Reflective.Set(entry, "Id", id));
            Assert.True(Reflective.Set(entry, "Simplified", simplified));
            Assert.True(Reflective.Set(entry, "English", english));
            Assert.True(Reflective.Set(entry, "Japanese", japanese));
            Assert.True(Reflective.Set(entry, "Traditional", traditional));
            return entry;
        }

        private static MapField<int, STRSkillLocalConfigure> Map(params STRSkillLocalConfigure[] entries)
        {
            var map = new MapField<int, STRSkillLocalConfigure>();
            foreach (var entry in entries) map[entry.Id] = entry;
            return map;
        }

        [Fact]
        public void ApplyMap_WritesTranslationAndFallsBackToSimplified()
        {
            var map = Map(
                Entry(101, "打击"),
                Entry(102, "防御"));

            var texts = new Dictionary<int, string> { { 101, "Strike" } };

            int changed = StrTableMerge.ApplyMap(map, texts, "English");

            Assert.Equal(2, changed);
            Assert.Equal("Strike", map[101].English);
            Assert.Equal("防御", map[102].English);   // 数据包没有 → 回退简中，不留空白
        }

        [Fact]
        public void ApplyMap_OverwritesExistingTranslationAndLeavesOtherColumns()
        {
            var map = Map(Entry(101, "打击", english: "Old", japanese: "旧", traditional: "舊"));
            var texts = new Dictionary<int, string> { { 101, "Strike" } };

            Assert.Equal(1, StrTableMerge.ApplyMap(map, texts, "English"));

            Assert.Equal("Strike", map[101].English);
            Assert.Equal("旧", map[101].Japanese);
            Assert.Equal("舊", map[101].Traditional);
            Assert.Equal("打击", map[101].Simplified);
        }

        [Fact]
        public void ApplyMap_IsIdempotent()
        {
            var map = Map(Entry(101, "打击"));
            var texts = new Dictionary<int, string> { { 101, "Strike" } };

            Assert.Equal(1, StrTableMerge.ApplyMap(map, texts, "English"));
            Assert.Equal(0, StrTableMerge.ApplyMap(map, texts, "English"));
        }

        [Fact]
        public void ApplyMap_UsesTargetColumnOnly()
        {
            var map = Map(Entry(101, "打击"));
            var texts = new Dictionary<int, string> { { 101, "ストライク" } };

            Assert.Equal(1, StrTableMerge.ApplyMap(map, texts, "Japanese"));

            Assert.Equal("ストライク", map[101].Japanese);
            Assert.Equal("", map[101].English);
        }
    }
}

