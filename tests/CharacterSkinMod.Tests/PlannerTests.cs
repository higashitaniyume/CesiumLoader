using CharacterSkinMod;
using CesiumLoader.CharacterSkins;
using Xunit;

namespace CharacterSkinMod.Tests;
public sealed class PlannerTests
{
    private static void Set(object obj,string name,object value) => obj.GetType().GetProperty(name)!.SetValue(obj,value);
    private static SkinStandingPaintingConfigureItem Skin(int id, string label)
    {
        var skin=new SkinStandingPaintingConfigureItem();
        Set(skin,"ItemID",id); Set(skin,"CharacterLabel",label); Set(skin,"Character","portrait-"+label);
        Set(skin,"Voice",id+10); Set(skin,"Index",id+20); Set(skin,"LongTextID",id+30); Set(skin,"MoveVfxId",id+40); Set(skin,"CharacterBattlleRes",id+50);
        return skin;
    }
    private static CharacterSkinBinding Rule(string id, int hero, int target, int source) => new() { Id=id, HeroId=hero, TargetSkinItemId=target, OfficialSourceSkinItemId=source };
    [Fact] public void SourceChainsReadOriginalSnapshotAndDoNotCopyIdentityOrGameplay()
    {
        var a=Skin(1,"A"); var b=Skin(2,"B"); var c=Skin(3,"C"); b.Bust.Add("bust-B"); b.Actions.Add("Idle"); b.SkinScale.Add(1.25f);
        var scheme=new CharacterSkinScheme { Id="chain", Rules=[Rule("a-to-b",114,1,2),Rule("b-to-c",114,2,3)] };
        var plans=SchemeRuntimePlan.Build(scheme,new() { [114]=[a,b,c] });
        Assert.Equal(2,plans.Count);
        Assert.Equal("B",plans[0].Source.CharacterLabel); Assert.Equal("C",plans[1].Source.CharacterLabel);
        Assert.Equal("A",plans[0].Applied.CharacterLabel); Assert.Equal("B",plans[1].Applied.CharacterLabel);
        Assert.Equal("portrait-B",plans[0].Applied.Character); Assert.Equal(new[]{"bust-B"},plans[0].Applied.Bust); Assert.Equal(new[]{"Idle"},plans[0].Applied.Actions);
        Assert.Equal(1.25f,plans[0].Applied.SkinScale[0]);
        Assert.Equal(a.ItemID,plans[0].Applied.ItemID); Assert.Equal(a.Index,plans[0].Applied.Index); Assert.Equal(a.Voice,plans[0].Applied.Voice);
        Assert.Equal(a.CharacterBattlleRes,plans[0].Applied.CharacterBattlleRes); Assert.Equal(a.MoveVfxId,plans[0].Applied.MoveVfxId); Assert.Equal(a.LongTextID,plans[0].Applied.LongTextID);
        Assert.Equal("A",a.CharacterLabel); Assert.Equal("B",b.CharacterLabel); Assert.Equal("C",c.CharacterLabel);
        plans[0].Applied.Bust[0]="changed"; Assert.Equal("bust-B",b.Bust[0]);
    }
    [Fact] public void WildcardExpansionUsesExactRuleForEachHeroAndKeepsAllOtherTargets()
    {
        var scheme=new CharacterSkinScheme { Id="multi", Rules=[Rule("wildcard",114,0,2),Rule("exact",114,2,1),Rule("other",102,0,9)] };
        var plans=SchemeRuntimePlan.Build(scheme,new() { [114]=[Skin(1,"A"),Skin(2,"B"),Skin(3,"C")], [102]=[Skin(9,"Other")] });
        Assert.Equal(4,plans.Count);
        Assert.Equal(new[]{"B","A","B","Other"},plans.Select(x=>x.Source.CharacterLabel));
        Assert.Equal(new[]{"A","B","C","Other"},plans.Select(x=>x.Applied.CharacterLabel));
        Assert.Equal(new[]{1,2,3,9},plans.Select(x=>x.Applied.ItemID));
    }
    [Theory] [InlineData(999,1,2)] [InlineData(114,999,2)] [InlineData(114,1,9)]
    public void UnknownHeroTargetOrCrossHeroSourceIsRejected(int hero,int target,int source)
    {
        var scheme=new CharacterSkinScheme { Id="invalid", Rules=[Rule("rule",hero,target,source)] };
        Assert.Throws<InvalidOperationException>(()=>SchemeRuntimePlan.Build(scheme,new() { [114]=[Skin(1,"A"),Skin(2,"B")], [102]=[Skin(9,"Other")] }));
    }
    [Fact] public void CustomOnlyTargetUsesItsOriginalVisualBaseAndIgnoresPackageLegacyTarget()
    {
        var scheme=new CharacterSkinScheme { Id="custom", Rules=[new() { Id="custom",HeroId=114,TargetSkinItemId=2,CustomPackageId="skin" }] };
        var plan=Assert.Single(SchemeRuntimePlan.Build(scheme,new() { [114]=[Skin(1,"A"),Skin(2,"B")] }));
        Assert.Null(plan.Source); Assert.Equal(2,plan.Applied.ItemID); Assert.Equal("B",plan.Applied.CharacterLabel);
    }
    [Fact] public void ListAndMapCopiesAreBothPatchedInPlaceAndCanBeRestoredWithoutChangingIdentity()
    {
        var original=Skin(1,"Original");
        var listGroup=new SkinStandingPaintingConfigure(); Set(listGroup,"Id",123); listGroup.SkinStandingPaintingConfigureItems.Add(original);
        var config=new SkinConfigure(); config.StandingPaintings.Add(listGroup); config.StandingPaintingDict.Add(123,listGroup.Clone());
        var copies=SchemeRuntimePlan.FindSkinCopies(config,123,1);
        Assert.Equal(2,copies.Count); Assert.NotSame(copies[0],copies[1]);
        var snapshots=copies.Select(x=>x.Clone()).ToArray();
        foreach(var copy in copies) { Set(copy,"Character","New"); copy.Bust.Clear(); copy.Bust.Add("NewBust"); }
        Assert.Equal("New",original.Character); Assert.Equal("New",config.StandingPaintingDict[123].SkinStandingPaintingConfigureItems[0].Character);
        for(int i=0;i<copies.Count;i++) { Set(copies[i],"Character",snapshots[i].Character); copies[i].Bust.Clear(); copies[i].Bust.Add(snapshots[i].Bust); }
        Assert.Same(original,config.StandingPaintings[0].SkinStandingPaintingConfigureItems[0]);
        Assert.Equal("portrait-Original",original.Character); Assert.Equal("portrait-Original",config.StandingPaintingDict[123].SkinStandingPaintingConfigureItems[0].Character);
    }
    [Fact] public void ActualAddressAndTextIdCollisionsAllowSameValueButRejectDifferentOverrides()
    {
        var registry=new SchemeConflictRegistry(); registry.Add("STRCharacter:123","name"); registry.Add("STRCharacter:123","name");
        Assert.Throws<InvalidOperationException>(()=>registry.Add("STRCharacter:123","different"));
        registry.Add("image:global-map","same.png"); registry.Add("image:global-map","same.png");
        Assert.Throws<InvalidOperationException>(()=>registry.Add("image:global-map","another.png"));
    }
}
