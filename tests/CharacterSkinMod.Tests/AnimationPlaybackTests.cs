using CesiumLoader.CharacterSkins;
using Xunit;

namespace CharacterSkinMod.Tests;

public sealed class AnimationPlaybackTests
{
    private static CharacterAnimation Sequence(int count, bool loop = false)
    {
        var animation = new CharacterAnimation { Fps = 30, Loop = loop };
        for (int i = 0; i < count; i++) animation.Frames.Add(i + ".png");
        return animation;
    }

    [Theory]
    [InlineData("Fight-attack_00063", "Fight-attack")]
    [InlineData("Fight_idle_attack_12", "Fight_idle_attack")]
    [InlineData("Fight_idle_attack", "Fight_idle_attack")]
    [InlineData("Hit-01_00001", "Hit-01")]
    [InlineData("Idle", "Idle")]
    public void OnlyNumericFrameSuffixIsRemoved(string sprite, string action)
    {
        Assert.Equal(action, AnimationPlayback.ActionName(sprite));
    }

    [Fact]
    public void ShortSequenceHoldsLastCustomFrameAfterCompletion()
    {
        var animation = Sequence(15);
        Assert.Equal(14, AnimationPlayback.Frame(animation, 1000));
        Assert.Equal(14, AnimationPlayback.Frame(animation, 10000));
    }

    [Fact]
    public void LongOneShotContinuesWhenOriginalActionEndsEarly()
    {
        var animation = Sequence(60);
        Assert.True(AnimationPlayback.ShouldFinish(animation, 1000));
        Assert.Equal(59, AnimationPlayback.Frame(animation, 1999));
        Assert.False(AnimationPlayback.ShouldFinish(animation, 2000));
    }

    [Fact]
    public void LoopingSequenceDoesNotBlockNextAction()
    {
        var animation = Sequence(15, true);
        Assert.False(AnimationPlayback.ShouldFinish(animation, 100));
        Assert.Equal(0, AnimationPlayback.Frame(animation, 1000));
    }

    [Fact]
    public void UnequalFrameDurationsDetermineCompletionAndFrameSelection()
    {
        var animation = Sequence(3);
        animation.DurationsMs.AddRange(new[] { 100, 250, 650 });
        Assert.Equal(1, AnimationPlayback.Frame(animation, 100));
        Assert.Equal(2, AnimationPlayback.Frame(animation, 350));
        Assert.True(AnimationPlayback.ShouldFinish(animation, 999));
        Assert.False(AnimationPlayback.ShouldFinish(animation, 1000));
    }
}
