using CesiumLoader.CharacterSkins;
using Xunit;

namespace CharacterSkinMod.Tests;

public sealed class ActorPlaybackTests
{
    private static CharacterAnimation Sequence(int count, double fps = 10, bool loop = false, string action = "Hit")
    {
        var animation = new CharacterAnimation { Fps = (float)fps, Loop = loop };
        for (int i = 0; i < count; i++) animation.Frames.Add($"animations/{action}/{action}_{i:D5}.png");
        return animation;
    }
    private static CharacterSkinManifest Manifest()
    {
        var manifest = new CharacterSkinManifest { Id = "test", HeroId = 114, SkinItemId = 100114001 };
        manifest.SpriteMappings["Walk_00000"] = "animations/Walk/Walk_00000.png";
        manifest.SpriteMappings["Walk_00001"] = "animations/Walk/Walk_00001.png";
        return manifest;
    }
    // 逐帧映射优先：动作名相同、原帧号变化时按原帧取图，与本地时钟和动画速率无关。
    [Fact]
    public void MappingFollowsOriginalFrameRegardlessOfRateAndTime()
    {
        var playback = new ActorPlayback();
        var manifest = Manifest();
        Assert.Equal("animations/Walk/Walk_00000.png", playback.Step(manifest, "Walk_00000", 10.0, 1.8f).File);
        var second = playback.Step(manifest, "Walk_00001", 10.5, 0.0f);
        Assert.Equal(PlaybackKind.Mapping, second.Kind);
        Assert.Equal("animations/Walk/Walk_00001.png", second.File);
        Assert.Equal("", second.MissingAction);
        Assert.Equal("Walk_00001", second.LayoutName);
        Assert.True(second.OriginalLayout == manifest.UseOriginalSpriteLayout);
    }
    // 游戏把动画调快时自定义序列必须同速推进（房间速度/回放倍速）。
    [Fact]
    public void SequenceAdvancesWithGameAnimationRate()
    {
        var manifest = new CharacterSkinManifest { Id = "test", HeroId = 114 };
        manifest.Animations["Hit"] = Sequence(20);
        var slow = new ActorPlayback();
        Assert.Equal("animations/Hit/Hit_00000.png", slow.Step(manifest, "Hit_00000", 0.0, 1f).File);
        Assert.Equal("animations/Hit/Hit_00003.png", slow.Step(manifest, "Hit_00001", 0.3, 1f).File);
        var fast = new ActorPlayback();
        Assert.Equal("animations/Hit/Hit_00000.png", fast.Step(manifest, "Hit_00000", 0.0, 1.8f).File);
        Assert.Equal("animations/Hit/Hit_00005.png", fast.Step(manifest, "Hit_00001", 0.3, 1.8f).File);
    }
    // 时停/暂停（速率为 0）时自定义序列冻结在当前帧。
    [Fact]
    public void SequenceFreezesWhileGameAnimationIsPaused()
    {
        var manifest = new CharacterSkinManifest { Id = "test", HeroId = 114 };
        manifest.Animations["Hit"] = Sequence(10);
        var playback = new ActorPlayback();
        Assert.Equal("animations/Hit/Hit_00000.png", playback.Step(manifest, "Hit_00000", 0.0, 1.8f).File);
        Assert.Equal("animations/Hit/Hit_00001.png", playback.Step(manifest, "Hit_00001", 0.1, 1.8f).File);
        Assert.Equal("animations/Hit/Hit_00001.png", playback.Step(manifest, "Hit_00002", 0.9, 0f).File);
        Assert.Equal("animations/Hit/Hit_00001.png", playback.Step(manifest, "Hit_00003", 5.0, 0f).File);
    }
    // 同一个非循环动作第二次连播：原帧号回退即从头重新播放，不能停在最后一帧。
    [Fact]
    public void RepeatedSameActionRestartsInsteadOfHoldingLastFrame()
    {
        var manifest = new CharacterSkinManifest { Id = "test", HeroId = 114 };
        manifest.Animations["Hit"] = Sequence(10);
        var playback = new ActorPlayback();
        Assert.Equal("animations/Hit/Hit_00000.png", playback.Step(manifest, "Hit_00000", 0.0, 1f).File);
        Assert.Equal("animations/Hit/Hit_00005.png", playback.Step(manifest, "Hit_00005", 0.5, 1f).File);
        Assert.Equal("animations/Hit/Hit_00009.png", playback.Step(manifest, "Hit_00009", 0.9, 1f).File);
        // 第二次播放：原帧号回到 0，自定义序列必须从第一帧重播。
        Assert.Equal("animations/Hit/Hit_00000.png", playback.Step(manifest, "Hit_00000", 1.0, 1f).File);
        Assert.Equal("animations/Hit/Hit_00001.png", playback.Step(manifest, "Hit_00001", 1.1, 1f).File);
    }
    // 变体动作名（游戏按皮肤给动作加后缀）按同一帧号取基础动作的自定义帧。
    [Theory]
    [InlineData("Idle-02_00007", "Idle_00007")]
    [InlineData("Talent-03_00012", "Talent_00012")]
    [InlineData("Fight-attack2_00003", "Fight-attack_00003")]
    public void VariantActionUsesBaseActionFrameWithSameIndex(string sprite, string baseSprite)
    {
        var manifest = new CharacterSkinManifest { Id = "test", HeroId = 115 };
        manifest.SpriteMappings[baseSprite] = "animations/base/" + baseSprite + ".png";
        var playback = new ActorPlayback();
        var decision = playback.Step(manifest, sprite, 0.0, 1.8f);
        Assert.Equal(PlaybackKind.Mapping, decision.Kind);
        Assert.Equal("animations/base/" + baseSprite + ".png", decision.File);
        // 锚点必须取基础动作那一帧，否则同一动作内 pivot 不一致会让人物抖动。
        Assert.Equal(baseSprite, decision.LayoutName);
        Assert.Equal("", decision.MissingAction);
    }
    // 基础动作的帧号没有对应映射时，退回基础动作的整段自定义序列。
    [Fact]
    public void VariantActionWithoutBaseFrameFallsBackToBaseSequence()
    {
        var manifest = new CharacterSkinManifest { Id = "test", HeroId = 115 };
        manifest.Animations["Fight-attack"] = Sequence(10, action: "Fight-attack");
        var playback = new ActorPlayback();
        Assert.Equal("animations/Fight-attack/Fight-attack_00000.png", playback.Step(manifest, "Fight-attack2_00000", 0.0, 1f).File);
        Assert.Equal("animations/Fight-attack/Fight-attack_00005.png", playback.Step(manifest, "Fight-attack2_00005", 0.5, 1f).File);
    }
    // 变体动作名解析：只去掉尾部的变体标记。
    [Theory]
    [InlineData("Idle-02", "Idle")]
    [InlineData("Talent-03", "Talent")]
    [InlineData("Fight-attack2", "Fight-attack")]
    [InlineData("Idle", "")]
    [InlineData("Walk", "")]
    [InlineData("Fight-move-loop", "")]
    [InlineData("", "")]
    public void VariantBaseStripsOnlyVariantSuffix(string action, string expected)
    {
        Assert.Equal(expected, AnimationPlayback.VariantBase(action));
    }
    [Fact]
    public void VariantSpriteKeepsFrameSuffix()
    {
        Assert.Equal("Idle_00007", AnimationPlayback.VariantSprite("Idle", "Idle-02_00007"));
        Assert.Equal("", AnimationPlayback.VariantSprite("Idle", "Idle-02"));
        Assert.Equal("", AnimationPlayback.VariantSprite("", "Idle-02_00007"));
    }
    // 映射动作里个别没有映射的原帧沿用最近一张自定义帧，不露出原图，也不算整个动作缺内容。
    [Fact]
    public void UnmappedFrameInsideMappedActionHoldsLastCustomFrame()
    {
        var playback = new ActorPlayback();
        var manifest = Manifest();
        Assert.Equal("animations/Walk/Walk_00000.png", playback.Step(manifest, "Walk_00000", 1.0, 1f).File);
        var held = playback.Step(manifest, "Walk_00007", 1.1, 1f);
        Assert.Equal(PlaybackKind.HoldCustom, held.Kind);
        Assert.Equal("animations/Walk/Walk_00000.png", held.File);
        Assert.Equal("Walk_00000", held.LayoutName);
        Assert.Equal("", held.MissingAction);
    }
    // 映射期间时钟照常累加：切到没有内容的动作时按正确位置继续上一段自定义序列。
    [Fact]
    public void MappingKeepsSequenceClockForLaterTakeover()
    {
        var manifest = new CharacterSkinManifest { Id = "test", HeroId = 114 };
        manifest.Animations["Hit"] = Sequence(10);
        manifest.SpriteMappings["Hit_00000"] = "animations/Hit/Hit_00000.png";
        manifest.SpriteMappings["Hit_00001"] = "animations/Hit/Hit_00001.png";
        var playback = new ActorPlayback();
        Assert.Equal(PlaybackKind.Mapping, playback.Step(manifest, "Hit_00000", 0.0, 1f).Kind);
        Assert.Equal(PlaybackKind.Mapping, playback.Step(manifest, "Hit_00001", 0.1, 1f).Kind);
        var held = playback.Step(manifest, "Other_00000", 0.4, 1f);
        Assert.Equal(PlaybackKind.Sequence, held.Kind);
        Assert.Equal("animations/Hit/Hit_00004.png", held.File);
    }
    // 完全没有内容的动作沿用上一张自定义帧并上报动作名，绝不换成游戏原版外观。
    [Fact]
    public void ActionWithoutContentReportsMissingAction()
    {
        var manifest = new CharacterSkinManifest { Id = "test", HeroId = 114 };
        var playback = new ActorPlayback();
        var decision = playback.Step(manifest, "Hit_00000", 0.0, 1f);
        Assert.Equal(PlaybackKind.HoldApplied, decision.Kind);
        Assert.Equal("Hit", decision.MissingAction);
    }
    // 只做了部分动作时，未做的动作至少播自定义 Idle 序列，保住换皮外观。
    [Fact]
    public void UnmappedActionFallsBackToCustomIdleSequence()
    {
        var manifest = new CharacterSkinManifest { Id = "test", HeroId = 114 };
        manifest.Animations["Idle"] = Sequence(10, loop: true, action: "Idle");
        var playback = new ActorPlayback();
        Assert.Equal("animations/Idle/Idle_00000.png", playback.Step(manifest, "Hit_00000", 0.0, 1f).File);
        var decision = playback.Step(manifest, "Hit_00001", 0.2, 1f);
        Assert.Equal(PlaybackKind.Sequence, decision.Kind);
        Assert.Equal("animations/Idle/Idle_00002.png", decision.File);
        Assert.Equal("", decision.MissingAction);
    }
    // 空精灵名不能抛出，也不能把动作状态打乱。
    [Fact]
    public void EmptySpriteNameIsIgnored()
    {
        var playback = new ActorPlayback();
        var manifest = Manifest();
        Assert.Equal(PlaybackKind.HoldApplied, playback.Step(manifest, "", 0.0, 1f).Kind);
        Assert.Equal(PlaybackKind.HoldApplied, playback.Step(manifest, null!, 0.0, 1f).Kind);
        Assert.Equal("animations/Walk/Walk_00000.png", playback.Step(manifest, "Walk_00000", 0.0, 1f).File);
    }
}
