using CesiumLoader.SDK.Speed;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.Shared.Speed;
using UnityEngine;
using Xunit;

namespace SpeedHackMod.Tests;

public sealed partial class SpeedHackControllerTests
{
    [Theory]
    [InlineData(1f, 4.0)]
    [InlineData(0.5f, 4.0)]
    [InlineData(1f, 100.0)]
    public void SavedPauseScale_DoesNotCompoundAndRestoresBaseline(float baseline, double speed)
    {
        float scale = baseline;
        var owner = new UnitySpeedScale(() => scale, value => scale = value);
        owner.Synchronize(speed); float saved = scale;
        scale = 0; owner.Synchronize(speed); Assert.Equal(0, scale);
        scale = saved; owner.Synchronize(speed); Assert.Equal(saved, scale);
        owner.Restore(); Assert.Equal(baseline, scale);
    }

    [Fact]
    public void DisableWhilePaused_DefersRestoreWithoutUnpausing()
    {
        float scale = 1;
        var owner = new UnitySpeedScale(() => scale, value => scale = value);
        owner.Synchronize(4); float saved = scale;
        scale = 0; owner.Synchronize(4); owner.Synchronize(1); Assert.Equal(0, scale);
        scale = saved; owner.Synchronize(1); Assert.Equal(1, scale);
        owner.Synchronize(4); Assert.Equal(4, scale);
    }

    [Fact]
    public void ChangedTargetWhilePaused_ReusesOriginalBaseline()
    {
        float scale = 1;
        var owner = new UnitySpeedScale(() => scale, value => scale = value);
        owner.Synchronize(4); scale = 0; owner.Synchronize(2);
        scale = 4; owner.Synchronize(2); Assert.Equal(2, scale);
        owner.Restore(); Assert.Equal(1, scale);
    }

    [Fact]
    public void ZeroRepeat_FiresOncePerHoldAndRearmsAfterRelease()
    {
        SpeedHack.Speed = 1;
        var mod = Create(); mod.Config.Set("repeatInterval", 0f); mod.OnInitialize();
        InputService.Held.Add(KeyCode.LeftAlt); InputService.Held.Add(KeyCode.Equals);
        Advance(mod, 100, .1f); Assert.Single(SpeedHack.Requests);
        InputService.Held.Clear(); mod.OnUpdate();
        InputService.Held.Add(KeyCode.LeftAlt); InputService.Held.Add(KeyCode.Equals);
        Advance(mod, 100, .1f); Assert.Equal(2, SpeedHack.Requests.Count);
        mod.OnUnload();
    }

    [Fact]
    public void LowerMaximumOnReload_ClampsActiveUnityTarget()
    {
        var mod = StartUnity(); mod.Config.Set("maxSpeed", 2.0); mod.OnConfigChanged();
        Assert.Equal(2, Time.Scale);
        Advance(mod, 20, .1f); Assert.Equal(2, Time.Scale); mod.OnUnload();
    }

    [Fact]
    public void ExternalNativeSpeedChange_SuspendsUnityAndRetriesLostResetAtBoundedRate()
    {
        var mod = StartUnity(); Assert.Equal(.08f, Time.Step, 5);
        SpeedHack.Speed = 4;
        Advance(mod, 10, .5f);
        Assert.Equal(1, Time.Scale); Assert.Equal(.02f, Time.Step, 5);
        Assert.Single(SpeedHack.Requests);
        Advance(mod, 12, .5f);
        Assert.Equal(2, SpeedHack.Requests.Count);
        Assert.All(SpeedHack.Requests, speed => Assert.Equal(1, speed));
        SpeedHack.Speed = 1; Advance(mod, 6, .5f);
        Assert.Equal(4, Time.Scale); Assert.Equal(.08f, Time.Step, 5);
        mod.OnUnload();
    }

    [Fact]
    public void LowerMaximumOnReload_RequestsClampedNativeSpeed()
    {
        var mod = Create(); mod.OnInitialize();
        mod.Config.Set("maxSpeed", 2.0); mod.OnConfigChanged();
        Assert.Equal(2, SpeedHack.Requested);
        // Do not optimize using the new target until the native engine acknowledges.
        Assert.Equal(.08f, Time.Step, 5);
        SpeedHack.Speed = 2; Advance(mod, 20, .1f); Assert.Equal(.04f, Time.Step, 5);
        mod.OnUnload();
    }

    [Fact]
    public void BoundsReload_DoesNotEnableTemporarilyDisabledUnitySpeed()
    {
        var mod = StartUnity(); Press(mod); Assert.Equal(1, Time.Scale);
        mod.Config.Set("minSpeed", 2.0); mod.Config.Set("maxSpeed", 3.0); mod.OnConfigChanged();
        Assert.Equal(1, Time.Scale); mod.OnUnload();
    }

    [Fact]
    public void BoundsReload_ReplacesAnOutOfRangeUnacknowledgedNativeRequest()
    {
        SpeedHack.Speed = 2;
        var mod = Create(); mod.OnInitialize();
        InputService.Held.Add(KeyCode.LeftAlt); InputService.Held.Add(KeyCode.Equals);
        mod.OnUpdate(); InputService.Held.Clear(); mod.OnUpdate();
        Assert.Equal(2.5, SpeedHack.Requested);
        mod.Config.Set("maxSpeed", 2.0); mod.OnConfigChanged();
        Assert.Equal(2, SpeedHack.Requested);
        mod.OnUnload();
    }

    [Fact]
    public void MissingAnimationTypes_RetryAtBoundedCadenceWithoutDuplicatingSources()
    {
        bool loaded = false; int lookups = 0, queries = 0;
        var animator = new FakeAnimator();
        var bridge = new IndependentAnimationBridge(n => { lookups++; return loaded && n == "UnityEngine.Animator" ? typeof(FakeAnimator) : null; },
            _ => { queries++; return new object[] { animator }; });
        bridge.Tick(0, 4); int initial = lookups;
        loaded = true;
        for (int i = 1; i < 50; i++) bridge.Tick(i * .1f, 4);
        Assert.Equal(initial, lookups); Assert.Equal(0, queries);
        bridge.Tick(5, 4); Assert.Equal(FakeAnimatorMode.Normal, animator.updateMode);
        bridge.Tick(10, 4); Assert.Equal(1, bridge.ChangedComponents);
        bridge.Restore(); Assert.Equal(FakeAnimatorMode.UnscaledTime, animator.updateMode);
        bridge.Tick(11, 4); Assert.Equal(1, bridge.ChangedComponents);
        bridge.Restore();
    }

    [Theory]
    [InlineData("version=2.8.6\r\nspeed=1.000\r\nhooks=4\r\n", 1.0)]
    [InlineData("speed=4.500\nhooks=4", 4.5)]
    public void NativeAcknowledgement_ParsesValidSpeedAndHooks(string state, double speed)
    {
        Assert.Equal(speed, NativeSpeedState.Parse(state));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("speed=1.000")]
    [InlineData("hooks=4")]
    [InlineData("speed=1\nhooks=0")]
    [InlineData("speed=1\nhooks=1.5")]
    [InlineData("speed=NaN\nhooks=4")]
    [InlineData("speed=Infinity\nhooks=4")]
    [InlineData("speed=1e0\nhooks=4")]
    [InlineData("speed=0.5\nhooks=4")]
    [InlineData("speed=101\nhooks=4")]
    [InlineData("speed=1x\nhooks=4")]
    [InlineData("speed=1.\nhooks=4")]
    [InlineData("speed=.1\nhooks=4")]
    [InlineData("speed=1.0.0\nhooks=4")]
    [InlineData("speed=1\nspeed=4\nhooks=4")]
    [InlineData("speed=1\nhooks=4\nhooks=0")]
    [InlineData("speed=1\nhooks=NaN")]
    [InlineData("speed=1\nhooks=-1")]
    public void NativeAcknowledgement_RejectsInvalidState(string state)
    {
        Assert.Null(NativeSpeedState.Parse(state));
    }

    [Fact]
    public void NativeAcknowledgement_RejectsOversizedState()
    {
        Assert.Null(NativeSpeedState.Parse(new string('x', 65537)));
    }

    [Fact]
    public void MalformedStartupState_DoesNotActivateUnityAcceleration()
    {
        SpeedHack.Speed = 1;
        string state = "hooks=4";
        var mod = Create(); mod.Config.Set("speed", 4.0); mod.Config.Set("accelerationMode", "unity");
        mod.ReadNativeSpeed = () => NativeSpeedState.Parse(state);
        mod.OnInitialize(); Advance(mod, 20, .5f); Assert.Equal(1f, Time.Scale);
        state = "hooks=4\nspeed=4.000";
        Advance(mod, 4, .5f); Assert.Equal(1f, Time.Scale); Assert.Equal(1, SpeedHack.Requested);
        state = "hooks=4\nspeed=1.000";
        Advance(mod, 4, .5f); Assert.Equal(4f, Time.Scale); mod.OnUnload();
    }

    [Fact]
    public void MissingStateDuringReset_DoesNotAcknowledgeSdkFallback()
    {
        var mod = Create(); mod.Config.Set("accelerationMode", "unity"); mod.OnInitialize();
        SpeedHack.IsAvailable = false; SpeedHack.StateReadable = false; SpeedHack.Speed = 1;
        Advance(mod, 20, .5f); Assert.Equal(1f, Time.Scale); Assert.Equal(0.02, Time.Step, 0.000001);
        int warnings = mod.Log.Warnings.Count;
        Advance(mod, 100, .5f); Assert.Equal(warnings, mod.Log.Warnings.Count);
        SpeedHack.IsAvailable = true; SpeedHack.StateReadable = true;
        Advance(mod, 4, .5f); Assert.Equal(4f, Time.Scale); mod.OnUnload();
    }

    [Fact]
    public void LostAcknowledgement_RestoresOwnershipUntilRecovery()
    {
        var mod = StartUnity();
        var root = new FairyGUI.GRoot(); var transition = new FairyGUI.Transition(root); root.AddTransition(transition);
        FairyGUI.GRoot.SetExisting(root); Advance(mod, 10, .1f);
        Assert.False(transition.ignoreEngineTimeScale);
        SpeedHack.StateReadable = false;
        Advance(mod, 4, .5f); Assert.Equal(1f, Time.Scale); Assert.Equal(0.02, Time.Step, 0.000001);
        Assert.True(transition.ignoreEngineTimeScale);
        SpeedHack.StateReadable = true; SpeedHack.Speed = 4;
        Advance(mod, 4, .5f); Assert.Equal(1f, Time.Scale); Assert.Equal(1, SpeedHack.Requested);
        SpeedHack.Speed = 1; Advance(mod, 4, .5f); Assert.Equal(4f, Time.Scale); Assert.Equal(0.08, Time.Step, 0.000001);
        mod.OnUnload();
    }

    [Fact]
    public void ComponentQuery_ContinuesPastFirstBatchWithBoundedWork()
    {
        var objects = Enumerable.Range(0, 1200).Select(_ => new FakeAnimator()).ToArray();
        int queries = 0;
        var bridge = new IndependentAnimationBridge(n => n == "UnityEngine.Animator" ? typeof(FakeAnimator) : null,
            _ => { queries++; return objects.Cast<object>().ToArray(); });
        bridge.Tick(0, 4); Assert.Equal(512, bridge.ChangedComponents); Assert.Equal(1, queries);
        bridge.Tick(.01f, 4); Assert.Equal(1024, bridge.ChangedComponents); Assert.Equal(1, queries);
        bridge.Tick(.02f, 4); Assert.Equal(1200, bridge.ChangedComponents); Assert.Equal(1, queries);
        Assert.All(objects, item => Assert.Equal(FakeAnimatorMode.Normal, item.updateMode));
        bridge.Restore();
        Assert.All(objects, item => Assert.Equal(FakeAnimatorMode.UnscaledTime, item.updateMode));
    }

    [Fact]
    public void Restore_CancelsUnfinishedComponentQueries()
    {
        var objects = Enumerable.Range(0, 600).Select(_ => new FakeAnimator()).ToArray();
        bool discover = true;
        var bridge = new IndependentAnimationBridge(n => n == "UnityEngine.Animator" ? typeof(FakeAnimator) : null,
            _ => discover ? objects.Cast<object>().ToArray() : Array.Empty<object>());
        bridge.Tick(0, 4); Assert.Equal(512, bridge.ChangedComponents);
        bridge.Restore(); discover = false; bridge.Tick(1, 4); Assert.Equal(0, bridge.ChangedComponents);
        Assert.All(objects, item => Assert.Equal(FakeAnimatorMode.UnscaledTime, item.updateMode));
    }

    [Fact]
    public void DestroyedPendingComponents_AreSkippedWithoutDroppingLaterEntries()
    {
        var objects = Enumerable.Range(0, 600).Select(_ => new FakeAnimator()).ToArray();
        var bridge = new IndependentAnimationBridge(n => n == "UnityEngine.Animator" ? typeof(FakeAnimator) : null,
            _ => objects.Cast<object>().ToArray());
        bridge.Tick(0, 4); objects[512].Destroyed = true; bridge.Tick(.01f, 4);
        Assert.Equal(599, bridge.ChangedComponents); Assert.Equal(FakeAnimatorMode.Normal, objects[599].updateMode);
        bridge.Restore();
    }

    [Fact]
    public void DestroyedComponents_ReleaseTrackingCapacityWithoutToggle()
    {
        var objects = Enumerable.Range(0, 4096).Select(_ => new FakeAnimator()).ToArray();
        object[] scene = objects.Cast<object>().ToArray();
        var bridge = new IndependentAnimationBridge(n => n == "UnityEngine.Animator" ? typeof(FakeAnimator) : null,
            _ => scene.ToArray());
        for (int frame = 0; frame < 8; frame++) bridge.Tick(frame * .01f, 4);
        Assert.Equal(4096, bridge.ChangedComponents);
        foreach (var item in objects) item.Destroyed = true;
        var next = new FakeAnimator(); scene = new object[] { next };
        bridge.Tick(1, 4);
        Assert.InRange(bridge.ChangedComponents, 4080, 4096);
        for (int frame = 0; frame < 300; frame++) bridge.Tick(1 + frame * .01f, 4);
        Assert.Equal(1, bridge.ChangedComponents); Assert.Equal(FakeAnimatorMode.Normal, next.updateMode);
        bridge.Restore(); Assert.Equal(FakeAnimatorMode.UnscaledTime, next.updateMode);
    }

    [Fact]
    public void ReplacedAndDestroyedVideoPlayers_ReleaseStaleTrackingRecords()
    {
        var controller = new FakeMovieController();
        var bridge = new IndependentAnimationBridge(n => n == "UI.CriManaMovieControllerForGGraph" ? typeof(FakeMovieController) : null,
            _ => new object[] { controller });
        bridge.Tick(0, 4); var old = controller.player;
        controller.player = new FakeMoviePlayer(); bridge.Tick(1, 2);
        Assert.Equal(1, bridge.ChangedComponents); Assert.Equal(1, bridge.ChangedVideos); Assert.Equal(2f, controller.player.Speed);
        controller.Destroyed = true; bridge.Tick(2, 2); Assert.Equal(0, bridge.ChangedComponents); Assert.Equal(0, bridge.ChangedVideos);
        bridge.Restore(); Assert.Equal(3f, old.Speed);
    }

    [Fact]
    public void DisposedUiTransitions_ReleaseCapacityWhileAccelerationStaysEnabled()
    {
        var root = new FairyGUI.GRoot();
        for (int i = 0; i < 4096; i++) root.AddTransition(new FairyGUI.Transition(root));
        FairyGUI.GRoot.SetExisting(root); var bridge = new FairyUiTimeScaleBridge();
        for (int frame = 0; frame < 70; frame++) bridge.Tick(frame * .01f);
        Assert.Equal(4096, bridge.TrackedTransitions); root.isDisposed = true;
        var nextRoot = new FairyGUI.GRoot(); var next = new FairyGUI.Transition(nextRoot); nextRoot.AddTransition(next);
        FairyGUI.GRoot.SetExisting(nextRoot);
        bridge.Tick(1);
        Assert.InRange(bridge.TrackedTransitions, 4080, 4096);
        for (int frame = 0; frame < 900; frame++) bridge.Tick(1 + frame * .01f);
        Assert.Equal(1, bridge.TrackedTransitions); Assert.False(next.ignoreEngineTimeScale);
        bridge.Restore(); Assert.True(next.ignoreEngineTimeScale);
    }

    [Fact]
    public void DisposedAnimationGearsAndExternalRoots_ReleaseCapacity()
    {
        var root = new FairyGUI.GRoot(); var clips = new List<FairyGUI.GMovieClip>();
        for (int i = 0; i < 4096; i++) { var clip = new FairyGUI.GMovieClip(); clips.Add(clip); root.AddChild(clip); }
        FairyGUI.GRoot.SetExisting(root); var bridge = new FairyUiTimeScaleBridge();
        for (int frame = 0; frame < 220; frame++) bridge.Tick(frame * .01f);
        Assert.Equal(4096, bridge.TrackedAnimations);
        foreach (var clip in clips) clip.isDisposed = true;
        root.isDisposed = true;
        var roots = new List<FairyGUI.GComponent>();
        for (int i = 0; i < 512; i++) { var panel = new FairyGUI.GComponent(); roots.Add(panel); bridge.RegisterExternalRoot(panel); }
        foreach (var panel in roots) panel.isDisposed = true;
        var nextRoot = new FairyGUI.GRoot(); var next = new FairyGUI.GMovieClip(); nextRoot.AddChild(next);
        FairyGUI.GRoot.SetExisting(nextRoot);
        for (int frame = 0; frame < 900; frame++) bridge.Tick(3 + frame * .01f);
        Assert.Equal(1, bridge.TrackedAnimations); Assert.Equal(0, bridge.ExternalRoots);
        var newPanel = new FairyGUI.GComponent(); bridge.RegisterExternalRoot(newPanel); Assert.Equal(1, bridge.ExternalRoots);
        Assert.False(next.ignoreEngineTimeScale);
        bridge.Restore(); Assert.True(next.ignoreEngineTimeScale);
    }
}
