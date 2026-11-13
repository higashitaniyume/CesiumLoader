using System;
using System.Linq;
using PinnedRelicsMod;
using Xunit;

namespace PinnedRelicsMod.Tests
{
    public sealed class RelicCatalogTests
    {
        [Fact]
        public void DefaultEightRelicsKeepOrder()
        {
            string[] unknown;
            var defaults = RelicCatalog.Select(RelicCatalog.DefaultNames, out unknown);
            Assert.True(unknown.Length == 0 && defaults.Select(r => r.Id).SequenceEqual(new[] {50024,50082,50041,50086,50078,50079,50010,50011}), "Default eight relics or order changed");
        }

        [Fact]
        public void ChineseNameCanExtendDefaults()
        {
            string[] unknown;
            var extended = RelicCatalog.Select(RelicCatalog.DefaultNames + ",星币锤", out unknown);
            Assert.True(unknown.Length == 0 && extended.Length == 9 && extended[8].Id == 50043, "Chinese-name addition failed");
        }

        [Fact]
        public void SeparatorsAndAliasesAreNormalized()
        {
            string[] unknown;
            var separated = RelicCatalog.Select(" 磨刀石，友情微章；肾上腺素－高效\n紫色飞星、金色飞星|星币锤", out unknown);
            Assert.True(unknown.Length == 0 && separated.Length == 6 && separated[0].Id == 50082 && separated[2].Id == 50024, "Separators or name normalization failed");
        }

        [Fact]
        public void DuplicatesKeepFirstOccurrence()
        {
            string[] unknown;
            var duplicate = RelicCatalog.Select("磨刀石,星币锤,磨刀石,友情徽章,友情微章", out unknown);
            Assert.True(duplicate.Select(r => r.Id).SequenceEqual(new[] {50082,50043,50041}), "Duplicate aliases or ordering failed");
        }

        [Fact]
        public void UnknownNamesAreReportedOnce()
        {
            string[] unknown;
            var misspelled = RelicCatalog.Select("飞星,不正确的名称,磨刀石,不正确的名称", out unknown);
            Assert.True(misspelled.Length == 1 && unknown.SequenceEqual(new[] {"飞星", "不正确的名称"}), "Unknown names are not diagnosed accurately");
        }

        [Fact]
        public void EmptyNamesHideAll()
        {
            string[] unknown;
            Assert.True(RelicCatalog.Select(" \r\n，、;", out unknown).Length == 0 && unknown.Length == 0, "Empty list does not clear selection");
        }

        [Fact]
        public void CatalogueHasNinetyUniqueNames()
        {
            string[] unknown;
            string allNames = string.Join(",", RelicCatalogData.Create().Select(r => r.Name));
            var all = RelicCatalog.Select(allNames, out unknown);
            Assert.True(all.Length == 90 && unknown.Length == 0, "Offline catalogue incomplete or contains duplicate names");
        }

        [Fact]
        public void BrokenWatchSupportsFullWidthParentheses()
        {
            string[] unknown;
            var brokenWatch = RelicCatalog.Select("时间怀表（破碎）", out unknown);
            Assert.True(unknown.Length == 0 && brokenWatch.Length == 1 && brokenWatch[0].Id == 50080, "Game-native broken-watch name missing");
        }

        [Fact]
        public void OwnedSelectionWorksBeyondThirtyTwoEntries()
        {
            string[] unknown;
            string allNames = string.Join(",", RelicCatalogData.Create().Select(r => r.Name));
            var all = RelicCatalog.Select(allNames, out unknown);
            var visible = new bool[all.Length];
            var owned = all.Where((_, i) => i >= 32).Select(r => r.Id).ToArray();
            int count = RelicCatalog.FillOwned(all, owned, visible);
            Assert.True(count == all.Length - 32 && !visible.Take(32).Any(v => v) && visible.Skip(32).All(v => v), "Selections beyond 32 entries are lost");
        }

        [Fact]
        public void InventoryRemovalClearsVisibility()
        {
            string[] unknown;
            string allNames = string.Join(",", RelicCatalogData.Create().Select(r => r.Name));
            var all = RelicCatalog.Select(allNames, out unknown);
            var visible = new bool[all.Length];
            RelicCatalog.FillOwned(all, new[] { all[0].Id, all[all.Length - 1].Id }, visible);
            Assert.True(RelicCatalog.FillOwned(all, null, visible) == 0 && !visible.Any(v => v), "Removed inventory leaves stale icons");
        }

        [Fact]
        public void RowFitsAvailableWidth()
        {
            float scale = RelicCatalog.FitScale(12, 30, 3, 180);
            Assert.True(scale > 0 && (12 * 30 + 11 * 3) * scale <= 180.001f, "Configured row overflows its available width");
        }

    }
}
