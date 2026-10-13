using CharacterSkinMod;
using Xunit;

public class LayoutAliasTests
{
    static T Set<T>(T value, string property, object data) { typeof(T).GetProperty(property)!.SetValue(value, data); return value; }
    static SkinStandingPaintingConfigureItem Skin(string key) => Set(Set(new SkinStandingPaintingConfigureItem(), "Character", key), "SfwCharacter", key + "_sfw");
    static SkinOffsetConfigure Layout(string key, float scale = .6f)
    {
        var value = new SkinOffsetConfigure();
        Set(value, "Id", 114); Set(value, "ImageName", key); Set(value, "OffsetX", -41f); Set(value, "OffsetY", 3f);
        Set(value, "CharacterStateSkinOffsetX", 206f); Set(value, "CharacterStateSkinOffsetY", -67f);
        Set(value, "CharacterStateSkinScale", scale); Set(value, "IsCharacterStateSkinFlipX", true);
        return value;
    }
    static SkinConfigure Config(params SkinOffsetConfigure[] values) { var config = new SkinConfigure(); config.Offsets.Add(values); foreach (var value in values) if (!config.OffsetDict.ContainsKey(value.Id)) config.OffsetDict.Add(value.Id, value.Clone()); return config; }
    static SkinLayoutAliases Aliases(SkinConfigure config, string slot = "Character")
    {
        var aliases = new SkinLayoutAliases(); aliases.Register("private", slot, config, Skin("package"), Skin("official"), Skin("original")); return aliases;
    }
    [Fact] public void WildcardPackageUsesFirstSkinLikeToysCropWhileExplicitIdSelectsExactSource()
    {
        var first = Set(Skin("first"), "ItemID", 11); var second = Set(Skin("second"), "ItemID", 12);
        var originals = new List<SkinStandingPaintingConfigureItem> { first, second };
        Assert.Same(first, SkinLayoutAliases.PackageSource(originals, 0));
        Assert.Same(second, SkinLayoutAliases.PackageSource(originals, 12));
        Assert.Null(SkinLayoutAliases.PackageSource(originals, 99));
    }
    [Fact] public void PrivateUrlPreservesPackageCanvasLayoutInsteadOfOfficialOrTarget()
    {
        var config = Config(Layout("package"), Layout("official", .3f), Layout("original", 1f));
        var aliases = Aliases(config); aliases.Apply(config);
        var alias = Assert.Single(aliases.Entries).Value;
        Assert.Equal("package", alias.Source); Assert.Equal("private", alias.Value.ImageName);
        Assert.Equal(-41f, alias.Value.OffsetX); Assert.Equal(3f, alias.Value.OffsetY);
        Assert.Equal(206f, alias.Value.CharacterStateSkinOffsetX); Assert.Equal(-67f, alias.Value.CharacterStateSkinOffsetY);
        Assert.Equal(.6f, alias.Value.CharacterStateSkinScale); Assert.True(alias.Value.IsCharacterStateSkinFlipX);
        Assert.Contains(config.Offsets, value => value.ImageName == "private");
        Assert.Single(config.OffsetDict); Assert.Equal("package", config.OffsetDict[114].ImageName);
    }
    [Theory]
    [InlineData("InGameCharacter", "package")]
    [InlineData("InGameCharacterSFW", "package_sfw")]
    [InlineData("SfwCharacter", "package_sfw")]
    public void HudFallbackAndSfwUseSameCanonicalImageKeyAsGame(string slot, string expected)
    {
        var aliases = Aliases(Config(Layout("package"), Layout("package_sfw")), slot);
        Assert.Equal(expected, Assert.Single(aliases.Entries).Value.Source);
    }
    [Fact] public void DedicatedHudImageTakesPriorityOverFullCharacterFallback()
    {
        var config = Config(Layout("hud", .25f), Layout("package")); var aliases = new SkinLayoutAliases();
        aliases.Register("private", "InGameCharacter", config, Set(Skin("package"), "InGameCharacter", "hud"), null!, Skin("original"));
        Assert.Equal("hud", Assert.Single(aliases.Entries).Value.Source);
    }
    [Theory]
    [InlineData(true, "official")]
    [InlineData(false, "original")]
    public void MissingPackageLayoutFallsBackToOfficialThenOriginal(bool officialAvailable, string expected)
    {
        var config = officialAvailable ? Config(Layout("official"), Layout("original")) : Config(Layout("original"));
        Assert.Equal(expected, Assert.Single(Aliases(config).Entries).Value.Source);
    }
    [Fact] public void MissingAllLayoutsDoesNotInventOffsets()
    {
        var config = Config(); var aliases = Aliases(config); aliases.Apply(config); Assert.Equal(0, aliases.Count); Assert.Empty(config.Offsets);
    }
    [Fact] public void BustListUsesCorrectIndex()
    {
        var skin = Skin("package"); skin.Bust.Add(new[] { "bust0", "bust1" });
        var aliases = new SkinLayoutAliases(); aliases.Register("private", "Bust1", Config(Layout("bust1")), skin, null!, null!);
        Assert.Equal("bust1", Assert.Single(aliases.Entries).Value.Source);
    }
    [Fact] public void RestoreRemovesOnlyAddedListReferenceAndPreservesOriginalMapCopy()
    {
        var original = Layout("package"); var config = Config(original); var map = config.OffsetDict[114]; var aliases = Aliases(config);
        aliases.Apply(config); config.Offsets.Add(Layout("another")); aliases.Restore();
        Assert.Equal(2, config.Offsets.Count); Assert.Same(original, config.Offsets[0]); Assert.Same(map, config.OffsetDict[114]);
    }
    [Fact] public void RestoreLeavesThirdPartyModifiedLayoutIntact()
    {
        var config = Config(Layout("package")); var aliases = Aliases(config); aliases.Apply(config);
        var alias = Assert.Single(aliases.Entries).Value.Value; Set(alias, "CharacterStateSkinScale", .9f); aliases.Restore();
        Assert.Contains(alias, config.Offsets);
    }
    [Fact] public void ExistingPrivateAddressRejectsAndRollbackDoesNotRemoveIt()
    {
        var config = Config(Layout("package"), Layout("private")); var aliases = Aliases(config);
        Assert.Throws<InvalidOperationException>(() => aliases.Apply(config)); aliases.Restore(); Assert.Equal(2, config.Offsets.Count);
    }
    sealed class Cached { public SkinOffsetConfigure Data = null!; }
    static Cached Create(SkinOffsetConfigure value) => new Cached { Data = value.Clone() };
    static bool Matches(Cached value, SkinOffsetConfigure expected) => value.Data.Equals(expected);
    [Fact] public void ExistingAndFutureCachesReceiveAliasesAndRestoreTheirOwnedEntries()
    {
        var config = Config(Layout("package")); var aliases = Aliases(config); aliases.Apply(config);
        var existing = new Dictionary<string, Cached> { ["package"] = Create(config.Offsets[0]) };
        var future = config.Offsets.ToDictionary(value => value.ImageName, Create);
        var journal = new LayoutAliasCaches<Cached>(); journal.Sync(existing, aliases.Entries, Create, Matches); journal.Sync(future, aliases.Entries, Create, Matches);
        Assert.Equal(.6f, existing["private"].Data.CharacterStateSkinScale); Assert.Equal(.6f, future["private"].Data.CharacterStateSkinScale);
        journal.Restore(Matches); Assert.False(existing.ContainsKey("private")); Assert.False(future.ContainsKey("private")); Assert.True(existing.ContainsKey("package")); Assert.True(future.ContainsKey("package"));
    }
    [Fact] public void RepeatSyncDoesNotClobberThirdPartyCacheChangesAndRestorePreservesThem()
    {
        var config = Config(Layout("package")); var aliases = Aliases(config); var journal = new LayoutAliasCaches<Cached>(); var cache = new Dictionary<string, Cached>();
        journal.Sync(cache, aliases.Entries, Create, Matches); var changed = Create(Layout("private", .9f)); cache["private"] = changed;
        journal.Sync(cache, aliases.Entries, Create, Matches); Assert.Same(changed, cache["private"]); journal.Restore(Matches); Assert.Same(changed, cache["private"]);
    }
}
