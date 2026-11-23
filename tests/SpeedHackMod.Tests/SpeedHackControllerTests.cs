using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Speed;
using CesiumLoader.SDK.UserInterface;
using UnityEngine;
using Xunit;

namespace SpeedHackMod.Tests;

// Unity and SDK stubs expose process-wide state, so these tests must not overlap.
[CollectionDefinition("SpeedHack runtime", DisableParallelization = true)]
public sealed class SpeedHackRuntimeCollection { }

[Collection("SpeedHack runtime")]
public sealed partial class SpeedHackControllerTests : IDisposable
{
    public SpeedHackControllerTests() => Reset();
    public void Dispose() => Reset();

    [Fact]
    public void NewlyPlayingCardTransition_IsSynchronizedBeforeTreeDiscovery()
    {
        FairyGUI.GRoot.SetExisting(new FairyGUI.GRoot());
        var cardResult = new FairyGUI.GComponent();
        var showCard = new FairyGUI.Transition(cardResult);
        FairyGUI.FakeTweenManager.SetActive(new FairyGUI.GTweener(showCard, new object()));
        var bridge = new FairyUiTimeScaleBridge(typeof(FairyGUI.FakeTweenManager));
        bridge.Tick(0);
        Assert.False(showCard.ignoreEngineTimeScale);
        bridge.Restore();
        Assert.True(showCard.ignoreEngineTimeScale);
    }

    [Fact]
    public void WorldSpaceBattleDiceUi_FollowsSpeedAndRestores()
    {
        FairyGUI.GRoot.SetExisting(new FairyGUI.GRoot());
        var battlePanel = new FairyGUI.GComponent(); var dice = new FairyGUI.GMovieClip();
        var transition = new FairyGUI.Transition(); battlePanel.AddChild(dice); battlePanel.AddTransition(transition);
        var world = new FakeWorldPanel(battlePanel);
        var uiBridge = new FairyUiTimeScaleBridge();
        var discovery = new IndependentAnimationBridge(n => n == "FairyGUI.UIPanel" ? typeof(FakeWorldPanel) : null,
            _ => new object[] { world });
        discovery.RegisterUiRoot = uiBridge.RegisterExternalRoot;
        discovery.Tick(0, 4); uiBridge.Tick(0);
        Assert.False(dice.ignoreEngineTimeScale);
        Assert.False(transition.ignoreEngineTimeScale);
        Assert.Equal(1, uiBridge.ExternalRoots);
        discovery.Tick(1, 4); Assert.Equal(1, uiBridge.ExternalRoots);
        uiBridge.Restore();
        Assert.True(dice.ignoreEngineTimeScale);
        Assert.True(transition.ignoreEngineTimeScale);
    }

    [Fact]
    public void IndependentCameraBlend_FollowsUnitySpeedAndRestores()
    {
        var camera = new FakeCameraBrain();
        var bridge = new IndependentAnimationBridge(n => n == "Cinemachine.CinemachineBrain" ? typeof(FakeCameraBrain) : null,
            _ => new object[] { camera });
        bridge.Tick(0, 4);
        Assert.False(camera.m_IgnoreTimeScale);
        bridge.Restore();
        Assert.True(camera.m_IgnoreTimeScale);
    }

    [Fact]
    public void IndependentAnimationClocks_UseGameTimeWithoutDoubleScaling()
    {
        var animator = new FakeAnimator(); var normal = new FakeAnimator { updateMode = FakeAnimatorMode.Normal };
        var director = new FakeDirector(); var manual = new FakeDirector { timeUpdateMode = FakeDirectorMode.Manual };
        var spine = new FakeSkeleton();
        var types = new Dictionary<string, Type>
        {
            ["UnityEngine.Animator"] = typeof(FakeAnimator), ["UnityEngine.Playables.PlayableDirector"] = typeof(FakeDirector),
            ["Spine.Unity.SkeletonAnimation"] = typeof(FakeSkeleton)
        };
        int discoveries = 0;
        var bridge = new IndependentAnimationBridge(n => types.GetValueOrDefault(n), t =>
        {
            discoveries++;
            return t == typeof(FakeAnimator) ? new object[] { animator, normal } : t == typeof(FakeDirector) ? new object[] { director, manual } : new object[] { spine };
        });
        for (int i = 0; i < 20; i++) bridge.Tick(i * 0.06f, 4);
        Assert.Equal(FakeAnimatorMode.Normal, animator.updateMode);
        Assert.Equal(FakeAnimatorMode.Normal, normal.updateMode);
        Assert.Equal(FakeDirectorMode.GameTime, director.timeUpdateMode);
        Assert.Equal(FakeDirectorMode.Manual, manual.timeUpdateMode);
        Assert.False(spine.unscaledTime);
        Assert.Equal(3, bridge.ChangedComponents);
        Assert.InRange(discoveries, 0, 20);
        bridge.Restore();
        Assert.Equal(FakeAnimatorMode.UnscaledTime, animator.updateMode);
        Assert.Equal(FakeDirectorMode.UnscaledGameTime, director.timeUpdateMode);
        Assert.True(spine.unscaledTime);
    }

    [Fact]
    public void SilentUiVideo_UsesThreeTimesCeilingAndRestores()
    {
        var controller = new FakeMovieController();
        var bridge = new IndependentAnimationBridge(n => n == "UI.CriManaMovieControllerForGGraph" ? typeof(FakeMovieController) : null,
            _ => new object[] { controller });
        bridge.Tick(0, 4); Assert.Equal(3f, controller.player.Speed); Assert.Equal(1, bridge.ChangedVideos);
        bridge.Tick(1, 2); Assert.Equal(2f, controller.player.Speed);
        controller.player.SetSpeed(1f); bridge.Tick(2, 2); Assert.Equal(2f, controller.player.Speed);
        Assert.Equal(FakeFrameDrop.Ten, controller.maxFrameDrop);
        bridge.Restore(); Assert.Equal(1f, controller.player.Speed);
        Assert.Equal(FakeFrameDrop.Two, controller.maxFrameDrop);
    }

    [Fact]
    public void SilentVideo_PreservesInfiniteFrameDrop()
    {
        var controller = new FakeMovieController { maxFrameDrop = FakeFrameDrop.Infinite };
        var bridge = new IndependentAnimationBridge(n => n == "UI.CriManaMovieControllerForGGraph" ? typeof(FakeMovieController) : null,
            _ => new object[] { controller });
        bridge.Tick(0, 4);
        Assert.Equal(3f, controller.player.Speed);
        Assert.Equal(FakeFrameDrop.Infinite, controller.maxFrameDrop);
        bridge.Restore();
        Assert.Equal(1f, controller.player.Speed);
        Assert.Equal(FakeFrameDrop.Infinite, controller.maxFrameDrop);
    }

    [Fact]
    public void VideoWithAudio_IsNotModified()
    {
        var controller = new FakeMovieController(); controller.player.movieInfo.numAudioStreams = 1;
        var bridge = new IndependentAnimationBridge(n => n == "UI.CriManaMovieControllerForGGraph" ? typeof(FakeMovieController) : null,
            _ => new object[] { controller });
        var reports = new List<string>(); bridge.Report = reports.Add;
        bridge.Tick(0, 4); bridge.Tick(0.25f, 4);
        Assert.Equal(0, controller.player.Writes); Assert.Equal(0, bridge.ChangedVideos);
        Assert.Equal(1, bridge.SkippedAudioVideos);
        Assert.Single(reports, message => message.Contains("numAudioStreams=1"));
        bridge.Restore(); Assert.Equal(0, controller.player.Writes);
    }

    [Fact]
    public void FightWindowVideo_MultipliesAndRestoresBattleRate()
    {
        var controller = new FakeMovieController();
        var callback = new UI.CriMovieManager.PlayCallback(new UI.FightWindow().ShowFightVideoLoader);
        controller.player.statusChangeCallback = callback.OnStatus;
        var bridge = new IndependentAnimationBridge(n => n switch
        {
            "UI.CriManaMovieControllerForGGraph" => typeof(FakeMovieController),
            "GameLogic.BattleConfig" => typeof(GameLogic.BattleConfig),
            _ => null
        }, _ => new object[] { controller });

        bridge.Tick(0, 2);
        Assert.Equal(2.5f, controller.player.Speed);
        bridge.Restore();
        Assert.Equal(1.25f, controller.player.Speed);
    }

    [Fact]
    public void ReusedVideo_WaitsForActivePlayingAndMovieInfo()
    {
        var controller = new FakeMovieController();
        var bridge = new IndependentAnimationBridge(
            n => n == "UI.CriManaMovieControllerForGGraph" ? typeof(FakeMovieController) : null,
            _ => throw new InvalidOperationException(), _ => new object[] { controller });
        bridge.Tick(0, 2);

        var player = controller.player;
        controller.isActiveAndEnabled = false;
        bridge.Tick(0.25f, 2);
        Assert.Equal(1, player.Writes);

        controller.isActiveAndEnabled = true;
        controller.CurrentVideoKey = "second";
        player.SetSpeed(1);
        player.movieInfo = null;
        player.status = FakeMovieStatus.WaitPrep;
        bridge.Tick(0.5f, 2);
        player.status = FakeMovieStatus.Playing;
        bridge.Tick(0.75f, 2);
        Assert.Equal(2, player.Writes);
        player.movieInfo = new FakeMovieInfo();
        bridge.Tick(1, 2);
        Assert.Equal(2f, player.Speed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorldVideo_ScalesGameRateAndLeavesTimelineInControl(bool fullscreen)
    {
        var quad = new FakeBattleQuad();
        string channel = fullscreen ? "Core.Unit.BattleFullScreenQuad" : "Core.Unit.BattleCustomVideoQuad";
        var bridge = new IndependentAnimationBridge(n => n == channel ? typeof(FakeBattleQuad) : null,
            _ => new object[] { quad });
        if (fullscreen)
        {
            bridge.Tick(0, 2);
            Assert.Equal(0, quad.controller.player.Writes);
            quad._syncWithTimeline = false;
        }
        bridge.Tick(1, 2);
        Assert.Equal(2.5f, quad.controller.player.Speed);
        bridge.Restore();
        Assert.Equal(1.25f, quad.controller.player.Speed);
    }

    [Fact]
    public void Unload_PreservesLaterAnimationClockOwner()
    {
        var director = new FakeDirector();
        var bridge = new IndependentAnimationBridge(n => n == "UnityEngine.Playables.PlayableDirector" ? typeof(FakeDirector) : null,
            _ => new object[] { director });
        bridge.Tick(0, 4); director.timeUpdateMode = FakeDirectorMode.Manual; bridge.Restore();
        Assert.Equal(FakeDirectorMode.Manual, director.timeUpdateMode);
    }

    [Fact]
    public void FairyGuiTransitionsAndClips_FollowUnitySpeedAndRestore()
    {
        var mod = StartUnity();
        var root = new FairyGUI.GRoot();
        var transition = new FairyGUI.Transition(); root.AddTransition(transition);
        var clip = new FairyGUI.GMovieClip(); root.AddChild(clip);
        FairyGUI.GRoot.SetExisting(root);
        Advance(mod, 5, 0.01f);
        Assert.Equal(0.4, transition.Advance(0.1f, 4), 0.000001);
        Assert.False(clip.ignoreEngineTimeScale);
        int writes = transition.Writes; Advance(mod, 300, 0.01f); Assert.Equal(writes, transition.Writes);
        Press(mod);
        Assert.True(transition.ignoreEngineTimeScale);
        Assert.True(clip.ignoreEngineTimeScale);
        Press(mod); Advance(mod, 5, 0.01f);
        Assert.False(transition.ignoreEngineTimeScale);
        mod.OnUnload();
        Assert.True(transition.ignoreEngineTimeScale);
    }

    [Fact]
    public void UiAnimationSynchronization_CannotBeDisabledByConfig()
    {
        var mod = StartUnity();
        var root = new FairyGUI.GRoot(); var transition = new FairyGUI.Transition(); root.AddTransition(transition);
        FairyGUI.GRoot.SetExisting(root); Advance(mod, 5, 0.01f);
        mod.Config.Set("accelerateUiAnimations", false); mod.OnConfigChanged(); Advance(mod, 10, 0.01f);
        Assert.False(transition.ignoreEngineTimeScale);
        Assert.False(mod.Config.Has("accelerateUiAnimations"));
        Assert.Equal(4f, Time.Scale); mod.OnUnload();
    }

    [Fact]
    public void UiScanning_UsesExistingVisibleUiWithoutCreatingRoot()
    {
        var bridge = new FairyUiTimeScaleBridge(); bridge.Tick(0);
        Assert.Equal(0, bridge.TrackedTransitions);
        var root = new FairyGUI.GRoot(); var hidden = new FairyGUI.GComponent { visible = false };
        var transition = new FairyGUI.Transition(); hidden.AddTransition(transition); root.AddChild(hidden);
        FairyGUI.GRoot.SetExisting(root); bridge.Tick(0);
        Assert.True(transition.ignoreEngineTimeScale);
        hidden.visible = true; bridge.Tick(1);
        Assert.False(transition.ignoreEngineTimeScale);
        bridge.Restore();
    }

    [Fact]
    public void LargeUiTrees_AreProcessedIncrementally()
    {
        var root = new FairyGUI.GRoot();
        for (int i = 0; i < 1000; i++) { var child = new FairyGUI.GComponent(); child.AddTransition(new FairyGUI.Transition()); root.AddChild(child); }
        FairyGUI.GRoot.SetExisting(root);
        var bridge = new FairyUiTimeScaleBridge(); bridge.Tick(0);
        Assert.InRange(bridge.TrackedTransitions, 0, 999);
        for (int frame = 0; frame < 200; frame++) bridge.Tick(frame * 0.01f);
        Assert.Equal(1000, bridge.TrackedTransitions); bridge.Restore();
    }

    [Fact]
    public void NewlyOpenedWindow_IsPrioritizedOverBackgroundSweep()
    {
        var root = new FairyGUI.GRoot();
        for (int i = 0; i < 1000; i++) root.AddChild(new FairyGUI.GComponent());
        FairyGUI.GRoot.SetExisting(root);
        var bridge = new FairyUiTimeScaleBridge(); bridge.Tick(0);
        var popup = new FairyGUI.GComponent(); var animation = new FairyGUI.Transition(); popup.AddTransition(animation);
        root.AddChild(popup); bridge.Tick(0.01f);
        Assert.False(animation.ignoreEngineTimeScale);
        bridge.Restore();
    }

    [Fact]
    public void UnityAcceleration_KeepsNativeClockAtNormalSpeed()
    {
        SpeedHack.Speed = 1;
        var mod = Create(); mod.Config.Set("speed", 4.0);
        mod.OnInitialize(); mod.OnUpdate();
        Assert.Equal(1, SpeedHack.Actual); Assert.Equal(4f, Time.Scale); Assert.Equal(0.08, Time.Step, 0.000001);
        Press(mod); Assert.Equal(1f, Time.Scale); Assert.Equal(0.02, Time.Step, 0.000001);
        Press(mod); Assert.Equal(4f, Time.Scale);
        mod.OnUnload(); Assert.Equal(1f, Time.Scale); Assert.Equal(0.02, Time.Step, 0.000001);
    }

    [Fact]
    public void UnityStartup_WaitsForNativeConfirmation()
    {
        var mod = Create();
        mod.OnInitialize(); mod.OnUpdate();
        Assert.Equal(1, SpeedHack.Requested); Assert.Equal(1f, Time.Scale);
        Advance(mod, 20, 0.5f); Assert.Equal(1f, Time.Scale);
        SpeedHack.Speed = 1; Advance(mod, 10, 0.5f);
        Assert.Equal(4f, Time.Scale); Assert.Equal(0.08, Time.Step, 0.000001);
        mod.OnUnload(); Assert.Equal(1f, Time.Scale);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unity")]
    [InlineData("native")]
    [InlineData("invalid")]
    public void Startup_AlwaysUsesUnityAndRemovesLegacyMode(string mode)
    {
        SpeedHack.Speed = 1;
        var mod = Create();
        mod.Config.Set("speed", 4.0);
        if (mode != null) mod.Config.Set("accelerationMode", mode);

        mod.OnInitialize();

        Assert.Equal(4f, Time.Scale);
        Assert.False(mod.Config.Has("accelerationMode"));
        Assert.Empty(SpeedHack.Requests);
        mod.OnUnload();
    }

    [Fact]
    public void ExternalNativeAcceleration_PausesUnityUntilNeutralized()
    {
        var mod = StartUnity();
        SpeedHack.Speed = 4; Advance(mod, 10, 0.5f);
        Assert.Equal(1f, Time.Scale); Assert.Equal(1, SpeedHack.Requested);
        SpeedHack.Speed = 1; Advance(mod, 10, 0.5f); Assert.Equal(4f, Time.Scale);
        mod.OnUnload();
    }

    [Fact]
    public void UnityMode_RespectsPauseAndResume()
    {
        var mod = StartUnity();
        Time.Scale = 0; Advance(mod, 10, 0.5f); Assert.Equal(0f, Time.Scale);
        Time.Scale = 1; Advance(mod, 10, 0.5f); Assert.Equal(4f, Time.Scale);
        mod.OnUnload(); Assert.Equal(1f, Time.Scale);
    }

    [Fact]
    public void UnityUnload_PreservesLaterTimeScaleOwner()
    {
        var mod = StartUnity(); Time.Scale = 0.7f; mod.OnUnload(); Assert.Equal(0.7f, Time.Scale);
    }

    [Fact]
    public void StableUnityFrames_DoNotRewritePropertiesOrNativeRequests()
    {
        var mod = StartUnity();
        int writes = Time.ScaleWrites; int requests = SpeedHack.Requests.Count;
        SpeedHack.Reads = 0; Advance(mod, 2400, 1f / 240f);
        Assert.Equal(writes, Time.ScaleWrites); Assert.Equal(requests, SpeedHack.Requests.Count);
        Assert.InRange(SpeedHack.Reads, 0, 21);
        mod.OnUnload();
    }

    [Fact]
    public void UnityMode_LocksFixedStepCompensationEnabled()
    {
        var mod = StartUnity();
        mod.Config.Set("optimizeFixedUpdates", false); mod.OnConfigChanged();
        Assert.Equal(4f, Time.Scale); Assert.Equal(0.08, Time.Step, 0.000001);
        Assert.False(mod.Config.Has("optimizeFixedUpdates"));
        Press(mod); Assert.Equal(1f, Time.Scale); Press(mod); Assert.Equal(4f, Time.Scale);
        mod.OnUnload();
    }

    [Fact]
    public void UnityInternalCallFailure_IsNotRepeatedEveryFrame()
    {
        SpeedHack.Speed = 1; Time.FailAccess = true;
        var mod = Create(); mod.Config.Set("speed", 4.0);
        mod.OnInitialize(); int warnings = mod.Log.Warnings.Count;
        Advance(mod, 1000, 0.1f); Assert.Equal(warnings, mod.Log.Warnings.Count);
        Time.FailAccess = false; mod.OnUnload();
    }

    [Fact]
    public void Toggle_RetainsUnityTargetWhileWaitingForNativeReset()
    {
        var mod = Create();
        mod.OnInitialize();
        Assert.Equal(1f, Time.Scale);
        Assert.Equal(1, SpeedHack.Requested);

        Press(mod);
        Advance(mod, 20, 0.5f);
        Press(mod);
        Assert.Equal(1f, Time.Scale);
        Assert.All(SpeedHack.Requests, speed => Assert.Equal(1, speed));

        SpeedHack.Speed = 1;
        Advance(mod, 10, 0.5f);
        Assert.Equal(4f, Time.Scale);
        mod.OnUnload();
    }

    [Fact]
    public void HeldKeyOrDuplicateUpdate_DoesNotToggleRepeatedly()
    {
        var mod = StartUnity();
        InputService.Held.Add(KeyCode.Delete);
        InputService.Pressed.Add(KeyCode.Delete);
        Advance(mod, 20, 0.01f);
        Assert.Equal(1f, Time.Scale);
        Assert.Equal(2, Time.ScaleWrites);

        InputService.Held.Clear();
        InputService.Pressed.Clear();
        mod.OnUpdate();
        Press(mod);
        Assert.Equal(4f, Time.Scale);
        Assert.Equal(3, Time.ScaleWrites);
        Assert.Empty(SpeedHack.Requests);
        mod.OnUnload();
    }

    [Fact]
    public void RepeatedToggleCycles_RetainSelectedSpeed()
    {
        var mod = StartUnity();
        for (int cycle = 0; cycle < 50; cycle++)
        {
            Press(mod);
            Assert.Equal(1f, Time.Scale);
            Press(mod);
            Assert.Equal(4f, Time.Scale);
        }
        Assert.Empty(SpeedHack.Requests);
        mod.OnUnload();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    [InlineData(10.0)]
    public void StartupSpeed_KeepsFiftyFixedUpdatesPerRealSecond(double speed)
    {
        var mod = StartUnity(speed);
        Assert.Equal(50, speed / Time.fixedDeltaTime, 0.001);
        mod.OnUnload(); Assert.Equal(0.02, Time.Step, 0.000001);
    }

    [Fact]
    public void SpeedChanges_RescaleFixedStepWithoutCompounding()
    {
        var mod = StartUnity();
        mod.Config.Set("speedStep", 2.0);
        mod.OnConfigChanged();
        InputService.Held.Add(KeyCode.LeftAlt);
        InputService.Held.Add(KeyCode.Minus);
        mod.OnUpdate();
        Assert.Equal(0.04, Time.Step, 0.000001);

        InputService.Held.Clear();
        Press(mod);
        Assert.Equal(0.02, Time.Step, 0.000001);
        Press(mod);
        Assert.Equal(0.04, Time.Step, 0.000001);
        Assert.Empty(SpeedHack.Requests);
        mod.OnUnload();
    }

    [Fact]
    public void Startup_ForcesLockedOptionsEnabledEvenWhenConfigSetsFalse()
    {
        Reset(); SpeedHack.Speed = 1;
        var root = new FairyGUI.GRoot(); var transition = new FairyGUI.Transition(); root.AddTransition(transition);
        FairyGUI.GRoot.SetExisting(root);

        var mod = Create();
        mod.Config.Set("speed", 4.0);
        mod.Config.Set("accelerationMode", "native");
        mod.Config.Set("optimizeFixedUpdates", false);
        mod.Config.Set("accelerateUiAnimations", false);
        mod.Config.Set("accelerateIndependentAnimations", false);
        mod.Config.Set(ModConfig.CommentsKey, new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["speed"] = "keep-speed",
            ["accelerationMode"] = "keep-mode",
            ["optimizeFixedUpdates"] = "stale",
            ["accelerateUiAnimations"] = "stale",
            ["accelerateIndependentAnimations"] = "stale"
        });
        mod.OnInitialize();
        Advance(mod, 5, 0.01f);

        Assert.False(mod.Config.Has("optimizeFixedUpdates"));
        Assert.False(mod.Config.Has("accelerateUiAnimations"));
        Assert.False(mod.Config.Has("accelerateIndependentAnimations"));
        var comments = mod.Config.Get<IDictionary<string, object>>(ModConfig.CommentsKey);
        Assert.NotNull(comments);
        Assert.False(comments.ContainsKey("optimizeFixedUpdates"));
        Assert.False(comments.ContainsKey("accelerateUiAnimations"));
        Assert.False(comments.ContainsKey("accelerateIndependentAnimations"));
        Assert.True(comments.ContainsKey("speed"));
        Assert.False(comments.ContainsKey("accelerationMode"));
        Assert.False(mod.Config.Has("accelerationMode"));
        Assert.Equal(4f, Time.Scale);
        Assert.Equal(0.08, Time.Step, 0.000001);
        Assert.False(transition.ignoreEngineTimeScale);
        Assert.Contains(mod.Log.Infos, m => m.Contains("accelerateUiAnimations=true(锁定)") && m.Contains("accelerateIndependentAnimations=true(锁定)"));

        mod.Config.Set("accelerationMode", "native"); mod.OnConfigChanged();
        Assert.False(mod.Config.Has("accelerationMode"));
        Assert.Equal(4f, Time.Scale);
        Assert.Empty(SpeedHack.Requests);

        // Unload flushes the remembered speed without restoring removed keys.
        InputService.Held.Add(KeyCode.LeftAlt); InputService.Held.Add(KeyCode.Equals);
        Advance(mod, 1, 0.01f);
        InputService.Held.Clear();
        mod.OnUnload();
        Assert.Equal(4.5, mod.Config.GetDouble("speed", 0));
        Assert.False(mod.Config.Has("accelerationMode"));
        Assert.False(mod.Config.Has("optimizeFixedUpdates"));
        Assert.False(mod.Config.Has("accelerateUiAnimations"));
        Assert.False(mod.Config.Has("accelerateIndependentAnimations"));
    }

    [Fact]
    public void Unload_PreservesLaterFixedStepWrite()
    {
        var mod = StartUnity(); Time.Step = 0.03f; mod.OnUnload(); Assert.Equal(0.03f, Time.Step);
    }

    [Fact]
    public void SceneTimestepReplacement_BecomesLatestBaseline()
    {
        var mod = StartUnity(); Time.Step = 0.01f; Advance(mod, 10, 0.5f); Assert.Equal(0.04, Time.Step, 0.000001);
        mod.OnUnload(); Assert.Equal(0.01, Time.Step, 0.000001);
    }

    [Fact]
    public void MaximumTimestep_CapsExtremeAcceleration()
    {
        var mod = StartUnity(100); Assert.Equal(1.0 / 3.0, Time.Step, 0.000001);
        mod.Config.Set("maxSpeed", 2.0); mod.OnConfigChanged();
        Assert.Equal(0.04, Time.Step, 0.000001);
        mod.OnUnload(); Assert.Equal(0.02, Time.Step, 0.000001);
    }

    [Fact]
    public void MissingFixedStepInternalCall_LogsOnceAndLeavesHotkeysWorking()
    {
        Time.FailStepAccess = true;
        SpeedHack.Speed = 1;
        var mod = Create();
        mod.Config.Set("speed", 4.0);
        mod.OnInitialize();
        int warnings = mod.Log.Warnings.Count;

        Advance(mod, 1000, 0.1f);
        Assert.Equal(warnings, mod.Log.Warnings.Count);
        Assert.Equal(4f, Time.Scale);
        Press(mod);
        Assert.Equal(1f, Time.Scale);
        Assert.Empty(SpeedHack.Requests);
        mod.OnUnload();
    }

    [Fact]
    public void ModUi_BlocksInputWithoutBlockingSynchronization()
    {
        var mod = StartUnity();
        UiService.ShouldBlockGameInput = true;
        Press(mod);
        Assert.Equal(4f, Time.Scale);

        SpeedHack.Speed = 4;
        Advance(mod, 10, 0.5f);
        Assert.Equal(1f, Time.Scale);
        Assert.Equal(0.02, Time.Step, 0.000001);
        Assert.Equal(1, SpeedHack.Requested);
        mod.OnUnload();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void InvalidSpeed_DoesNotWriteInvalidTimestep(double speed)
    {
        float current = 0.02f;
        var budget = new FixedStepBudget(() => current, () => 1f / 3f, v => current = v);
        budget.Synchronize(speed); Assert.Equal(0.02f, current);
    }

    [Fact]
    public void FailedRestoration_IsRetryable()
    {
        float current = 0.02f; bool fail = false;
        var budget = new FixedStepBudget(() => current, () => 1f / 3f, v =>
        {
            if (fail) throw new InvalidOperationException("fixture setter failure"); current = v;
        });
        budget.Synchronize(4); fail = true;
        Assert.Throws<InvalidOperationException>(budget.Restore);
        Assert.Equal(0.08, current, 0.000001); fail = false; budget.Restore(); Assert.Equal(0.02f, current);
    }

    private static SpeedHackController Create()
    {
        var mod = new SpeedHackController
        {
            ReadNativeSpeed = () => SpeedHack.IsAvailable && SpeedHack.StateReadable ? SpeedHack.Speed : (double?)null
        };
        mod.Config.Set("maxSpeed", 100.0); return mod;
    }

    private static SpeedHackController StartUnity(double speed = 4.0)
    {
        Reset(); SpeedHack.Speed = 1;
        var mod = Create(); mod.Config.Set("speed", speed);
        mod.OnInitialize(); mod.OnUpdate(); return mod;
    }

    private static void Reset()
    {
        Time.Step = 0.02f; Time.maximumDeltaTime = 1f / 3f; Time.Writes = 0; Time.FailStepAccess = false; Time.FailAccess = false;
        Time.Scale = 1f; Time.ScaleWrites = 0;
        FairyGUI.GRoot.SetExisting(null); FairyGUI.FakeTweenManager.SetActive();
        SpeedHack.Reset();
        UnityTime.RealtimeSinceStartup = 0; InputService.Held.Clear(); InputService.Pressed.Clear();
        UiService.ShouldBlockGameInput = false;
    }

    private static void Advance(SpeedHackController mod, int frames, float delta)
    {
        for (int frame = 0; frame < frames; frame++) { UnityTime.RealtimeSinceStartup += delta; mod.OnUpdate(); }
    }

    private static void Press(SpeedHackController mod)
    {
        InputService.Pressed.Add(KeyCode.Delete); InputService.Held.Add(KeyCode.Delete);
        mod.OnUpdate(); InputService.Pressed.Clear(); InputService.Held.Clear(); mod.OnUpdate();
    }
}
