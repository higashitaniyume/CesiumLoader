using SpeedHackMod;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Speed;
using CesiumLoader.SDK.UserInterface;
using UnityEngine;

namespace SpeedHackMod.Tests;

internal static partial class Program
{
    private static int _passed, _failed;

    private static int Main()
    {
        RunReviewRegressions();
        Run("a newly playing card transition is synchronized immediately before tree discovery", () =>
        {
            FairyGUI.GRoot.SetExisting(new FairyGUI.GRoot());
            var cardResult = new FairyGUI.GComponent();
            var showCard = new FairyGUI.Transition(cardResult);
            FairyGUI.FakeTweenManager.SetActive(new FairyGUI.GTweener(showCard, new object()));
            var bridge = new FairyUiTimeScaleBridge(typeof(FairyGUI.FakeTweenManager));
            bridge.Tick(0);
            if (showCard.ignoreEngineTimeScale) throw new Exception("Card transition still waits for the UI tree sweep");
            bridge.Restore();
            if (!showCard.ignoreEngineTimeScale) throw new Exception("Card transition not restored");
            FairyGUI.FakeTweenManager.SetActive();
        });
        Run("world-space battle dice UI outside GRoot follows speed and restores", () =>
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
            if (dice.ignoreEngineTimeScale || transition.ignoreEngineTimeScale) throw new Exception("World-space dice was missed");
            Equal(1, uiBridge.ExternalRoots);
            discovery.Tick(1, 4); Equal(1, uiBridge.ExternalRoots);
            uiBridge.Restore();
            if (!dice.ignoreEngineTimeScale || !transition.ignoreEngineTimeScale) throw new Exception("World UI flags not restored");
        });
        Run("independent Cinemachine entry blend follows Unity speed and restores", () =>
        {
            var camera = new FakeCameraBrain();
            var bridge = new IndependentAnimationBridge(n => n == "Cinemachine.CinemachineBrain" ? typeof(FakeCameraBrain) : null,
                _ => new object[] { camera });
            bridge.Tick(0, 4);
            if (camera.m_IgnoreTimeScale) throw new Exception("Camera entry blend still ignores speed");
            bridge.Restore();
            if (!camera.m_IgnoreTimeScale) throw new Exception("Camera clock not restored");
        });
        Run("independent Animator, Timeline and Spine clocks use game time without double scaling", () =>
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
            Equal((int)FakeAnimatorMode.Normal, (int)animator.updateMode);
            Equal((int)FakeAnimatorMode.Normal, (int)normal.updateMode);
            Equal((int)FakeDirectorMode.GameTime, (int)director.timeUpdateMode);
            Equal((int)FakeDirectorMode.Manual, (int)manual.timeUpdateMode);
            if (spine.unscaledTime) throw new Exception("Spine still uses unscaled clock");
            Equal(3, bridge.ChangedComponents);
            if (discoveries > 20) throw new Exception("Multiple native discoveries in one tick");
            bridge.Restore();
            Equal((int)FakeAnimatorMode.UnscaledTime, (int)animator.updateMode);
            Equal((int)FakeDirectorMode.UnscaledGameTime, (int)director.timeUpdateMode);
            if (!spine.unscaledTime) throw new Exception("Spine clock not restored");
        });
        Run("silent UI video uses supported 3x ceiling, speed changes and 1x restoration", () =>
        {
            var controller = new FakeMovieController();
            var bridge = new IndependentAnimationBridge(n => n == "UI.CriManaMovieControllerForGGraph" ? typeof(FakeMovieController) : null,
                _ => new object[] { controller });
            bridge.Tick(0, 4); Equal(3, controller.player.Speed); Equal(1, bridge.ChangedVideos);
            bridge.Tick(1, 2); Equal(2, controller.player.Speed);
            int writes = controller.player.Writes; bridge.Tick(2, 2); Equal(writes, controller.player.Writes);
            bridge.Restore(); Equal(1, controller.player.Speed);
        });
        Run("video clips with audio are not modified during playback", () =>
        {
            var controller = new FakeMovieController(); controller.player.movieInfo.numAudioStreams = 1;
            var bridge = new IndependentAnimationBridge(n => n == "UI.CriManaMovieControllerForGGraph" ? typeof(FakeMovieController) : null,
                _ => new object[] { controller });
            bridge.Tick(0, 4); Equal(0, controller.player.Writes); Equal(0, bridge.ChangedVideos);
            bridge.Restore(); Equal(0, controller.player.Writes);
        });
        Run("unload preserves a later animation clock owner", () =>
        {
            var director = new FakeDirector();
            var bridge = new IndependentAnimationBridge(n => n == "UnityEngine.Playables.PlayableDirector" ? typeof(FakeDirector) : null,
                _ => new object[] { director });
            bridge.Tick(0, 4); director.timeUpdateMode = FakeDirectorMode.Manual; bridge.Restore();
            Equal((int)FakeDirectorMode.Manual, (int)director.timeUpdateMode);
        });
        Run("independent FairyGUI transitions and movie clips follow Unity speed and restore", () =>
        {
            var mod = StartUnity();
            var root = new FairyGUI.GRoot();
            var transition = new FairyGUI.Transition(); root.AddTransition(transition);
            var clip = new FairyGUI.GMovieClip(); root.AddChild(clip);
            FairyGUI.GRoot.SetExisting(root);
            Advance(mod, 5, 0.01f);
            Equal(0.4, transition.Advance(0.1f, 4));
            if (clip.ignoreEngineTimeScale) throw new Exception("Movie clip still ignores Unity speed");
            int writes = transition.Writes; Advance(mod, 300, 0.01f); Equal(writes, transition.Writes);
            Press(mod);
            if (!transition.ignoreEngineTimeScale || !clip.ignoreEngineTimeScale) throw new Exception("UI flags not restored at 1x");
            Press(mod); Advance(mod, 5, 0.01f);
            if (transition.ignoreEngineTimeScale) throw new Exception("UI did not resume acceleration");
            mod.OnUnload();
            if (!transition.ignoreEngineTimeScale) throw new Exception("UI not restored on unload");
        });
        Run("UI animation synchronization can be disabled independently", () =>
        {
            var mod = StartUnity();
            var root = new FairyGUI.GRoot(); var transition = new FairyGUI.Transition(); root.AddTransition(transition);
            FairyGUI.GRoot.SetExisting(root); Advance(mod, 5, 0.01f);
            mod.Config.Set("accelerateUiAnimations", false); mod.OnConfigChanged(); Advance(mod, 10, 0.01f);
            if (!transition.ignoreEngineTimeScale) throw new Exception("UI flag not restored after disabling");
            Equal(4, Time.Scale); mod.OnUnload();
        });
        Run("UI scanning uses only existing visible UI and never creates a root", () =>
        {
            FairyGUI.GRoot.SetExisting(null);
            var bridge = new FairyUiTimeScaleBridge(); bridge.Tick(0);
            Equal(0, bridge.TrackedTransitions);
            var root = new FairyGUI.GRoot(); var hidden = new FairyGUI.GComponent { visible = false };
            var transition = new FairyGUI.Transition(); hidden.AddTransition(transition); root.AddChild(hidden);
            FairyGUI.GRoot.SetExisting(root); bridge.Tick(0);
            if (!transition.ignoreEngineTimeScale) throw new Exception("Hidden subtree was modified");
            hidden.visible = true; bridge.Tick(1);
            if (transition.ignoreEngineTimeScale) throw new Exception("Visible UI not discovered");
            bridge.Restore();
        });
        Run("large UI trees are processed incrementally rather than in a single update", () =>
        {
            var root = new FairyGUI.GRoot();
            for (int i = 0; i < 1000; i++) { var child = new FairyGUI.GComponent(); child.AddTransition(new FairyGUI.Transition()); root.AddChild(child); }
            FairyGUI.GRoot.SetExisting(root);
            var bridge = new FairyUiTimeScaleBridge(); bridge.Tick(0);
            if (bridge.TrackedTransitions >= 1000) throw new Exception("Unbounded UI traversal");
            for (int frame = 0; frame < 200; frame++) bridge.Tick(frame * 0.01f);
            Equal(1000, bridge.TrackedTransitions); bridge.Restore();
        });
        Run("a newly opened top window is accelerated before a long background UI sweep", () =>
        {
            var root = new FairyGUI.GRoot();
            for (int i = 0; i < 1000; i++) root.AddChild(new FairyGUI.GComponent());
            FairyGUI.GRoot.SetExisting(root);
            var bridge = new FairyUiTimeScaleBridge(); bridge.Tick(0);
            var popup = new FairyGUI.GComponent(); var animation = new FairyGUI.Transition(); popup.AddTransition(animation);
            root.AddChild(popup); bridge.Tick(0.01f);
            if (animation.ignoreEngineTimeScale) throw new Exception("Short popup animation waits behind old UI");
            bridge.Restore();
        });
        Run("Unity acceleration keeps the native clock at 1x while scaling game time", () =>
        {
            Reset(); SpeedHack.Speed = 1;
            var mod = Create(); mod.Config.Set("speed", 4.0); mod.Config.Set("accelerationMode", "unity");
            mod.OnInitialize(); mod.OnUpdate();
            Equal(1, SpeedHack.Actual); Equal(4, Time.Scale); Equal(0.08, Time.Step);
            Press(mod); Equal(1, Time.Scale); Equal(0.02, Time.Step);
            Press(mod); Equal(4, Time.Scale);
            mod.OnUnload(); Equal(1, Time.Scale); Equal(0.02, Time.Step);
        });
        Run("Unity startup waits for native 1x confirmation before applying any multiplier", () =>
        {
            Reset();
            var mod = Create(); mod.Config.Set("accelerationMode", "unity");
            mod.OnInitialize(); mod.OnUpdate();
            Equal(1, SpeedHack.Requested); Equal(1, Time.Scale);
            Advance(mod, 20, 0.5f); Equal(1, Time.Scale);
            SpeedHack.Speed = 1; Advance(mod, 10, 0.5f);
            Equal(4, Time.Scale); Equal(0.08, Time.Step);
            mod.OnUnload(); Equal(1, Time.Scale);
        });
        Run("switching acceleration modes never multiplies native and Unity speed", () =>
        {
            var mod = Start(4);
            mod.Config.Set("accelerationMode", "unity"); mod.OnConfigChanged();
            Equal(1, Time.Scale); Equal(1, SpeedHack.Requested);
            SpeedHack.Speed = 1; Advance(mod, 10, 0.5f); Equal(4, Time.Scale);
            mod.Config.Set("accelerationMode", "native"); mod.OnConfigChanged();
            Equal(1, Time.Scale); Equal(4, SpeedHack.Requested);
            SpeedHack.Speed = 4; Advance(mod, 10, 0.5f); Equal(0.08, Time.Step);
            mod.OnUnload();
        });
        Run("external native acceleration pauses the Unity multiplier until neutralized", () =>
        {
            var mod = StartUnity();
            SpeedHack.Speed = 4; Advance(mod, 10, 0.5f);
            Equal(1, Time.Scale); Equal(1, SpeedHack.Requested);
            SpeedHack.Speed = 1; Advance(mod, 10, 0.5f); Equal(4, Time.Scale);
            mod.OnUnload();
        });
        Run("Unity mode respects game pause and reapplies multiplier after resume", () =>
        {
            var mod = StartUnity();
            Time.Scale = 0; Advance(mod, 10, 0.5f); Equal(0, Time.Scale);
            Time.Scale = 1; Advance(mod, 10, 0.5f); Equal(4, Time.Scale);
            mod.OnUnload(); Equal(1, Time.Scale);
        });
        Run("Unity unload preserves a later timeScale owner", () =>
        {
            var mod = StartUnity(); Time.Scale = 0.7f; mod.OnUnload(); Equal(0.7, Time.Scale);
        });
        Run("Unity mode has no per-frame property writes or native speed requests", () =>
        {
            var mod = StartUnity();
            int writes = Time.ScaleWrites; int requests = SpeedHack.Requests.Count;
            SpeedHack.Reads = 0; Advance(mod, 2400, 1f / 240f);
            Equal(writes, Time.ScaleWrites); Equal(requests, SpeedHack.Requests.Count);
            if (SpeedHack.Reads > 21) throw new Exception("Native polling exceeds twice per real second");
            mod.OnUnload();
        });
        Run("Unity mode can disable fixed-step compensation without disabling speed", () =>
        {
            var mod = StartUnity();
            mod.Config.Set("optimizeFixedUpdates", false); mod.OnConfigChanged();
            Equal(4, Time.Scale); Equal(0.02, Time.Step);
            Press(mod); Equal(1, Time.Scale); Press(mod); Equal(4, Time.Scale);
            mod.OnUnload();
        });
        Run("Unity internal-call failure is not repeated every frame", () =>
        {
            Reset(); SpeedHack.Speed = 1; Time.FailAccess = true;
            var mod = Create(); mod.Config.Set("accelerationMode", "unity"); mod.Config.Set("speed", 4.0);
            mod.OnInitialize(); int warnings = mod.Log.Warnings.Count;
            Advance(mod, 1000, 0.1f); Equal(warnings, mod.Log.Warnings.Count);
            Time.FailAccess = false; mod.OnUnload();
        });
        Run("toggle can resume while native state acknowledgement is delayed", () =>
        {
            var mod = Start(4);
            InputService.Pressed.Add(KeyCode.Delete); mod.OnUpdate(); InputService.Pressed.Clear();
            Equal(1, SpeedHack.Requested);
            // Replay the real session: request.txt was updated, state.txt still says 4x.
            Advance(mod, 3, 0.5f);
            InputService.Pressed.Add(KeyCode.Delete); mod.OnUpdate(); InputService.Pressed.Clear();
            Equal(4, SpeedHack.Requested);
            mod.OnUnload();
        });
        Run("long-stale state cannot trap toggle at 1x, even with optimization disabled", () =>
        {
            var mod = Start(4, false);
            Press(mod);
            Equal(1, SpeedHack.Requested);
            Advance(mod, 200, 0.5f);
            int warnings = mod.Log.Warnings.Count;
            Advance(mod, 100, 0.5f);
            Equal(warnings, mod.Log.Warnings.Count);
            Press(mod);
            Equal(4, SpeedHack.Requested);
            mod.OnUnload();
        });
        Run("a held key or duplicate update never toggles repeatedly", () =>
        {
            var mod = Start(4);
            InputService.Held.Add(KeyCode.Delete);
            InputService.Pressed.Add(KeyCode.Delete);
            Advance(mod, 20, 0.01f);
            Equal(1, SpeedHack.Requests.Count);
            InputService.Held.Clear(); InputService.Pressed.Clear(); mod.OnUpdate();
            Press(mod); Equal(4, SpeedHack.Requested); Equal(2, SpeedHack.Requests.Count);
            mod.OnUnload();
        });
        Run("50 acknowledged off/on cycles retain selected acceleration", () =>
        {
            var mod = Start(4);
            for (int cycle = 0; cycle < 50; cycle++)
            {
                Press(mod); Equal(1, SpeedHack.Requested);
                SpeedHack.Speed = 1; Advance(mod, 4, 0.5f);
                Press(mod); Equal(4, SpeedHack.Requested);
                SpeedHack.Speed = 4; Advance(mod, 4, 0.5f);
            }
            mod.OnUnload();
        });
        foreach (double speed in new[] { 1.0, 2.0, 4.0, 10.0 })
            Run($"startup {speed}x keeps 50 fixed updates per real second", () =>
            {
                var mod = Start(speed);
                double scheduled = speed / Time.fixedDeltaTime;
                Console.WriteLine($"  {speed}x: fixedDeltaTime={Time.fixedDeltaTime:0.000}, scheduled={scheduled:0}/real second");
                Equal(50, scheduled, 0.001);
                mod.OnUnload();
                Equal(0.02, Time.Step);
            });

        Run("stable frames have bounded state polling and no redundant writes", () =>
        {
            var mod = Start(4);
            int writes = Time.Writes;
            SpeedHack.Reads = 0;
            Advance(mod, 2400, 4f / 240f);
            Equal(writes, Time.Writes);
            if (SpeedHack.Reads > 21) throw new Exception($"Too many state reads: {SpeedHack.Reads}");
            Console.WriteLine($"  2400 idle frames: {SpeedHack.Reads} state reads, {Time.Writes - writes} timestep writes");
            mod.OnUnload();
        });
        Run("speed changes rescale from baseline without compounding", () =>
        {
            var mod = Start(4);
            SpeedHack.Speed = 2; Advance(mod, 10, 0.5f); Equal(0.04, Time.Step);
            SpeedHack.Speed = 1; Advance(mod, 10, 0.5f); Equal(0.02, Time.Step);
            SpeedHack.Speed = 4; Advance(mod, 10, 0.5f); Equal(0.08, Time.Step);
            mod.OnUnload();
        });
        Run("asynchronous request waits for actual speed, then restores promptly", () =>
        {
            var mod = Start(4);
            InputService.Pressed.Add(KeyCode.Delete); mod.OnUpdate(); InputService.Pressed.Clear();
            Equal(1, SpeedHack.Requested); Equal(0.08, Time.Step);
            SpeedHack.Speed = 1;
            UnityTime.RealtimeSinceStartup = 0.4f;
            mod.OnUpdate(); Equal(0.02, Time.Step);
            mod.OnUnload();
        });
        Run("failed request leaves physics unchanged", () =>
        {
            var mod = Start(4);
            SpeedHack.AcceptRequest = false;
            InputService.Pressed.Add(KeyCode.Delete); mod.OnUpdate(); InputService.Pressed.Clear();
            Equal(0.08, Time.Step); mod.OnUnload();
        });
        Run("configuration can disable and re-enable optimization", () =>
        {
            var mod = Start(4);
            mod.Config.Set("optimizeFixedUpdates", false); mod.OnConfigChanged();
            Equal(0.02, Time.Step);
            SpeedHack.Reads = 0; Advance(mod, 120, 0.1f); Equal(0, SpeedHack.Reads);
            Equal(4, SpeedHack.Actual);
            mod.Config.Set("optimizeFixedUpdates", true); mod.OnConfigChanged(); Equal(0.08, Time.Step);
            mod.OnUnload();
        });
        Run("disabled at startup preserves original semantics", () =>
        {
            var mod = Start(4, false); Equal(0.02, Time.Step); Equal(0, Time.Writes); mod.OnUnload();
        });
        Run("unload preserves a later external write", () =>
        {
            var mod = Start(4); Time.Step = 0.03f; mod.OnUnload(); Equal(0.03, Time.Step);
        });
        Run("scene timestep replacement becomes latest baseline", () =>
        {
            var mod = Start(4); Time.Step = 0.01f; Advance(mod, 10, 0.5f); Equal(0.04, Time.Step);
            mod.OnUnload(); Equal(0.01, Time.Step);
        });
        Run("maximum timestep caps extreme acceleration", () =>
        {
            var mod = Start(100); Equal(1.0 / 3.0, Time.Step);
            SpeedHack.Speed = 2; Advance(mod, 3, 50f); Equal(0.04, Time.Step);
            mod.OnUnload(); Equal(0.02, Time.Step);
        });
        Run("missing ECall logs once and leaves hotkeys working", () =>
        {
            Reset(); Time.FailAccess = true;
            var mod = Create(); mod.OnInitialize();
            int warnings = mod.Log.Warnings.Count;
            Advance(mod, 1000, 0.1f); Equal(warnings, mod.Log.Warnings.Count);
            InputService.Pressed.Add(KeyCode.Delete); mod.OnUpdate(); InputService.Pressed.Clear();
            Equal(1, SpeedHack.Requested); mod.OnUnload();
        });
        Run("mod UI blocks input without blocking synchronization", () =>
        {
            var mod = Start(4); UiService.ShouldBlockGameInput = true;
            SpeedHack.Speed = 1; Advance(mod, 10, 0.5f); Equal(0.02, Time.Step); mod.OnUnload();
        });
        Run("late engine availability enables optimization", () =>
        {
            Reset(); SpeedHack.IsAvailable = false;
            var mod = Create(); mod.OnInitialize(); Equal(0.02, Time.Step);
            SpeedHack.IsAvailable = true; Advance(mod, 10, 0.5f); Equal(0.08, Time.Step); mod.OnUnload();
        });
        Run("invalid speed never writes an invalid Unity timestep", () =>
        {
            float current = 0.02f;
            var budget = new FixedStepBudget(() => current, () => 1f / 3f, v => current = v);
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1 })
            { budget.Synchronize(invalid); Equal(0.02, current); }
        });
        Run("failed restoration is retryable", () =>
        {
            float current = 0.02f; bool fail = false;
            var budget = new FixedStepBudget(() => current, () => 1f / 3f, v =>
            {
                if (fail) throw new InvalidOperationException("fixture setter failure"); current = v;
            });
            budget.Synchronize(4); fail = true;
            try { budget.Restore(); } catch (InvalidOperationException) { }
            Equal(0.08, current); fail = false; budget.Restore(); Equal(0.02, current);
        });
        Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed (controller/scheduler model; not in-game FPS)");
        return _failed == 0 ? 0 : 1;
    }

    private static SpeedHackController Start(double speed, bool enabled = true)
    {
        Reset(); SpeedHack.Speed = speed;
        var mod = Create(); mod.Config.Set("optimizeFixedUpdates", enabled);
        mod.OnInitialize(); mod.OnUpdate(); return mod;
    }
    private static SpeedHackController Create()
    {
        var mod = new SpeedHackController
        {
            ReadNativeSpeed = () => SpeedHack.IsAvailable && SpeedHack.StateReadable ? SpeedHack.Speed : (double?)null
        };
        mod.Config.Set("maxSpeed", 100.0); return mod;
    }
    private static SpeedHackController StartUnity()
    {
        Reset(); SpeedHack.Speed = 1;
        var mod = Create(); mod.Config.Set("speed", 4.0); mod.Config.Set("accelerationMode", "unity");
        mod.OnInitialize(); mod.OnUpdate(); return mod;
    }
    private static void Reset()
    {
        Time.Step = 0.02f; Time.maximumDeltaTime = 1f / 3f; Time.Writes = 0; Time.FailAccess = false;
        Time.Scale = 1f; Time.ScaleWrites = 0;
        FairyGUI.GRoot.SetExisting(null);
        SpeedHack.IsAvailable = true; SpeedHack.AcceptRequest = true; SpeedHack.Speed = 4; SpeedHack.Reads = 0;
        SpeedHack.StateReadable = true;
        SpeedHack.Requests.Clear();
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
    private static void Equal(double expected, double actual, double tolerance = 0.000001)
    {
        if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, got {actual}");
    }
    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS: " + name); _passed++; }
        catch (Exception error) { Console.WriteLine("FAIL: " + name + ": " + error.Message); _failed++; }
    }
}
