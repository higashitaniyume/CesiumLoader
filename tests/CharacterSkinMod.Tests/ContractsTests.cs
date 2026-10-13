using CesiumLoader.CharacterSkins;
using CesiumLoader.SDK.Configuration;
using Xunit;

namespace CharacterSkinMod.Tests;

public sealed class ContractsTests
{
    [Fact]
    public void SdkJsonReadsMultiHeroMixedSourcesAndSettings()
    {
        const string json = "{\"SchemaVersion\":1,\"Id\":\"daily\",\"Name\":\"日常方案\",\"Rules\":[{\"Id\":\"blue\",\"HeroId\":114,\"TargetSkinItemId\":100114001,\"OfficialSourceSkinItemId\":100114002,\"CustomPackageId\":\"blue-custom\"},{\"Id\":\"fenny\",\"HeroId\":102,\"TargetSkinItemId\":0,\"OfficialSourceSkinItemId\":100102002,\"CustomPackageId\":\"\"}]}";
        var scheme = CesiumJson.Deserialize<CharacterSkinScheme>(json);
        Assert.NotNull(scheme);
        CharacterSkinSchemeRules.Validate(scheme);
        var roundTrip = CesiumJson.Deserialize<CharacterSkinScheme>(CesiumJson.SerializePretty(scheme));
        Assert.Equal("日常方案", roundTrip.Name);
        Assert.Equal(2, roundTrip.Rules.Count);
        Assert.Equal("blue-custom", roundTrip.Rules[0].CustomPackageId);
        Assert.Equal(100114002, roundTrip.Rules[0].OfficialSourceSkinItemId);
        Assert.Equal("fenny", CharacterSkinSchemeRules.Resolve(roundTrip, 102, 100102001)!.Id);
        var settings = CesiumJson.Deserialize<CharacterSkinSettings>("{\"Enabled\":true,\"ActiveSchemeId\":\"daily\",\"ActivePackages\":[]}");
        Assert.Equal("daily", settings.ActiveSchemeId);
        Assert.Empty(settings.ActivePackages);
    }

    [Theory]
    [InlineData("CharacterMap")]
    [InlineData("Name")]
    [InlineData("asset:shared")]
    public void ConflictingSharedOverridesAreRejected(string slot)
    {
        var a = new CharacterSkinManifest { Id="a", HeroId=114 };
        var b = new CharacterSkinManifest { Id="b", HeroId=114 };
        if (slot=="Name") { a.Texts[slot]="A"; b.Texts[slot]="B"; }
        else { a.Images[slot]="images/a.png"; b.Images[slot]="images/b.png"; }
        var scheme = Scheme("a", "b");
        Assert.Throws<InvalidOperationException>(() => CharacterSkinSchemeRules.ValidateSharedOverrides(scheme, new Dictionary<string,CharacterSkinManifest> { ["a"]=a,["b"]=b }));
    }

    [Fact]
    public void SamePackageCanSupplySharedResourcesToMultipleTargets()
    {
        var package = new CharacterSkinManifest { Id="a",HeroId=114 };
        package.Images["CharacterMap"]="images/map.png";package.Texts["Name"]="同一名字";
        CharacterSkinSchemeRules.ValidateSharedOverrides(Scheme("a","a"),new Dictionary<string,CharacterSkinManifest> { ["a"]=package });
    }

    [Fact]
    public void StandardPortraitSlotsAreIndependentAcrossSkinTargets()
    {
        var a=new CharacterSkinManifest { Id="a",HeroId=114 };a.Images["Character"]="images/a.png";
        var b=new CharacterSkinManifest { Id="b",HeroId=114 };b.Images["Character"]="images/b.png";
        CharacterSkinSchemeRules.ValidateSharedOverrides(Scheme("a","b"),new Dictionary<string,CharacterSkinManifest> { ["a"]=a,["b"]=b });
    }

    private static CharacterSkinScheme Scheme(string a,string b) => new()
    {
        Id="daily",Rules=new() {
            new() { Id="one",HeroId=114,TargetSkinItemId=100114001,CustomPackageId=a },
            new() { Id="two",HeroId=114,TargetSkinItemId=100114002,CustomPackageId=b }
        }
    };
}
