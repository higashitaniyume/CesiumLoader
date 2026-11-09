using CesiumLoader.SDK.Speed;
using UnityEngine;

namespace SpeedHackMod.Tests;

internal static partial class Program
{
    private static void RunReviewRegressions()
    {
        Run("native acknowledgement requires valid speed and live hooks", () =>
        {
            Equal(1, NativeSpeedState.Parse("version=2.8.6\r\nspeed=1.000\r\nhooks=4\r\n").Value);
            Equal(4.5, NativeSpeedState.Parse("speed=4.500\nhooks=4").Value);
            foreach (string invalid in new[]
            {
                null, "", "speed=1.000", "hooks=4", "speed=1\nhooks=0", "speed=1\nhooks=1.5",
                "speed=NaN\nhooks=4", "speed=Infinity\nhooks=4", "speed=1e0\nhooks=4",
                "speed=0.5\nhooks=4", "speed=101\nhooks=4", "speed=1x\nhooks=4",
                "speed=1.\nhooks=4", "speed=.1\nhooks=4", "speed=1.0.0\nhooks=4",
                "speed=1\nspeed=4\nhooks=4", "speed=1\nhooks=4\nhooks=0",
                "speed=1\nhooks=NaN", "speed=1\nhooks=-1", new string('x', 65537)
            })
                if (NativeSpeedState.Parse(invalid).HasValue) throw new Exception("Invalid acknowledgement accepted: " + invalid);
        });
        Run("malformed startup state cannot activate Unity acceleration", () =>
        {
            Reset(); SpeedHack.Speed = 1;
            string state = "hooks=4";
            var mod = Create(); mod.Config.Set("speed", 4.0); mod.Config.Set("accelerationMode", "unity");
            mod.ReadNativeSpeed = () => NativeSpeedState.Parse(state);
            mod.OnInitialize(); Advance(mod, 20, .5f); Equal(1, Time.Scale);
            state = "hooks=4\nspeed=4.000";
            Advance(mod, 4, .5f); Equal(1, Time.Scale); Equal(1, SpeedHack.Requested);
            state = "hooks=4\nspeed=1.000";
            Advance(mod, 4, .5f); Equal(4, Time.Scale); mod.OnUnload();
        });
        Run("missing state during native reset never acknowledges SDK fallback 1x", () =>
        {
            Reset();
            var mod = Create(); mod.Config.Set("accelerationMode", "unity"); mod.OnInitialize();
            SpeedHack.IsAvailable = false; SpeedHack.StateReadable = false; SpeedHack.Speed = 1;
            Advance(mod, 20, .5f); Equal(1, Time.Scale); Equal(.02, Time.Step);
            int warnings = mod.Log.Warnings.Count;
            Advance(mod, 100, .5f); Equal(warnings, mod.Log.Warnings.Count);
            SpeedHack.IsAvailable = true; SpeedHack.StateReadable = true;
            Advance(mod, 4, .5f); Equal(4, Time.Scale); mod.OnUnload();
        });
        Run("lost acknowledgement restores Unity animation and physics ownership until recovery", () =>
        {
            var mod = StartUnity();
            var root = new FairyGUI.GRoot(); var transition = new FairyGUI.Transition(root); root.AddTransition(transition);
            FairyGUI.GRoot.SetExisting(root); Advance(mod, 10, .1f);
            if (transition.ignoreEngineTimeScale) throw new Exception("Initial UI adaptation missing");
            SpeedHack.StateReadable = false;
            Advance(mod, 4, .5f); Equal(1, Time.Scale); Equal(.02, Time.Step);
            if (!transition.ignoreEngineTimeScale) throw new Exception("Lost-state UI ownership retained");
            SpeedHack.StateReadable = true; SpeedHack.Speed = 4;
            Advance(mod, 4, .5f); Equal(1, Time.Scale); Equal(1, SpeedHack.Requested);
            SpeedHack.Speed = 1; Advance(mod, 4, .5f); Equal(4, Time.Scale); Equal(.08, Time.Step);
            mod.OnUnload();
        });
        Run("component query continues past 512 with a bounded per-frame batch", () =>
        {
            var objects = Enumerable.Range(0, 1200).Select(_ => new FakeAnimator()).ToArray();
            int queries = 0;
            var bridge = new IndependentAnimationBridge(n => n == "UnityEngine.Animator" ? typeof(FakeAnimator) : null,
                _ => { queries++; return objects.Cast<object>().ToArray(); });
            bridge.Tick(0, 4); Equal(512, bridge.ChangedComponents); Equal(1, queries);
            bridge.Tick(.01f, 4); Equal(1024, bridge.ChangedComponents); Equal(1, queries);
            bridge.Tick(.02f, 4); Equal(1200, bridge.ChangedComponents); Equal(1, queries);
            if (objects.Any(x => x.updateMode != FakeAnimatorMode.Normal)) throw new Exception("Query tail was missed");
            bridge.Restore();
            if (objects.Any(x => x.updateMode != FakeAnimatorMode.UnscaledTime)) throw new Exception("Batch restoration missed objects");
        });
        Run("restore cancels unfinished component queries", () =>
        {
            var objects = Enumerable.Range(0, 600).Select(_ => new FakeAnimator()).ToArray();
            bool discover = true;
            var bridge = new IndependentAnimationBridge(n => n == "UnityEngine.Animator" ? typeof(FakeAnimator) : null,
                _ => discover ? objects.Cast<object>().ToArray() : Array.Empty<object>());
            bridge.Tick(0, 4); Equal(512, bridge.ChangedComponents);
            bridge.Restore(); discover = false; bridge.Tick(1, 4); Equal(0, bridge.ChangedComponents);
            if (objects.Any(x => x.updateMode != FakeAnimatorMode.UnscaledTime)) throw new Exception("Pending batch survived restore");
        });
        Run("destroyed pending components are skipped without dropping later entries", () =>
        {
            var objects = Enumerable.Range(0, 600).Select(_ => new FakeAnimator()).ToArray();
            var bridge = new IndependentAnimationBridge(n => n == "UnityEngine.Animator" ? typeof(FakeAnimator) : null,
                _ => objects.Cast<object>().ToArray());
            bridge.Tick(0, 4); objects[512].Destroyed = true; bridge.Tick(.01f, 4);
            Equal(599, bridge.ChangedComponents); Equal((int)FakeAnimatorMode.Normal, (int)objects[599].updateMode);
            bridge.Restore();
        });
        Run("destroyed components release tracking capacity without a speed toggle", () =>
        {
            var objects = Enumerable.Range(0, 4096).Select(_ => new FakeAnimator()).ToArray();
            object[] scene = objects.Cast<object>().ToArray();
            var bridge = new IndependentAnimationBridge(n => n == "UnityEngine.Animator" ? typeof(FakeAnimator) : null,
                _ => scene.ToArray());
            for (int frame = 0; frame < 8; frame++) bridge.Tick(frame * .01f, 4);
            Equal(4096, bridge.ChangedComponents);
            foreach (var item in objects) item.Destroyed = true;
            var next = new FakeAnimator(); scene = new object[] { next };
            bridge.Tick(1, 4);
            if (bridge.ChangedComponents < 4080) throw new Exception("Unbounded component cleanup in one frame");
            for (int frame = 0; frame < 300; frame++) bridge.Tick(1 + frame * .01f, 4);
            Equal(1, bridge.ChangedComponents); Equal((int)FakeAnimatorMode.Normal, (int)next.updateMode);
            bridge.Restore(); Equal((int)FakeAnimatorMode.UnscaledTime, (int)next.updateMode);
        });
        Run("replaced and destroyed video players release stale tracking records", () =>
        {
            var controller = new FakeMovieController();
            var bridge = new IndependentAnimationBridge(n => n == "UI.CriManaMovieControllerForGGraph" ? typeof(FakeMovieController) : null,
                _ => new object[] { controller });
            bridge.Tick(0, 4); var old = controller.player;
            controller.player = new FakeMoviePlayer(); bridge.Tick(1, 2);
            Equal(1, bridge.ChangedComponents); Equal(1, bridge.ChangedVideos); Equal(2, controller.player.Speed);
            controller.Destroyed = true; bridge.Tick(2, 2); Equal(0, bridge.ChangedComponents); Equal(0, bridge.ChangedVideos);
            bridge.Restore(); Equal(3, old.Speed);
        });
        Run("disposed UI transitions release capacity while acceleration stays enabled", () =>
        {
            var root = new FairyGUI.GRoot();
            for (int i = 0; i < 4096; i++) root.AddTransition(new FairyGUI.Transition(root));
            FairyGUI.GRoot.SetExisting(root); var bridge = new FairyUiTimeScaleBridge();
            for (int frame = 0; frame < 70; frame++) bridge.Tick(frame * .01f);
            Equal(4096, bridge.TrackedTransitions); root.isDisposed = true;
            var nextRoot = new FairyGUI.GRoot(); var next = new FairyGUI.Transition(nextRoot); nextRoot.AddTransition(next);
            FairyGUI.GRoot.SetExisting(nextRoot);
            bridge.Tick(1);
            if (bridge.TrackedTransitions < 4080) throw new Exception("Unbounded UI cleanup in one frame");
            for (int frame = 0; frame < 900; frame++) bridge.Tick(1 + frame * .01f);
            Equal(1, bridge.TrackedTransitions);
            if (next.ignoreEngineTimeScale) throw new Exception("New transition blocked by disposed cache entries");
            bridge.Restore(); if (!next.ignoreEngineTimeScale) throw new Exception("Live transition not restored");
        });
        Run("disposed animation gears and external UI roots release capacity", () =>
        {
            var root = new FairyGUI.GRoot(); var clips = new List<FairyGUI.GMovieClip>();
            for (int i = 0; i < 4096; i++) { var clip = new FairyGUI.GMovieClip(); clips.Add(clip); root.AddChild(clip); }
            FairyGUI.GRoot.SetExisting(root); var bridge = new FairyUiTimeScaleBridge();
            for (int frame = 0; frame < 220; frame++) bridge.Tick(frame * .01f);
            Equal(4096, bridge.TrackedAnimations);
            foreach (var clip in clips) clip.isDisposed = true;
            root.isDisposed = true;
            var roots = new List<FairyGUI.GComponent>();
            for (int i = 0; i < 512; i++) { var panel = new FairyGUI.GComponent(); roots.Add(panel); bridge.RegisterExternalRoot(panel); }
            foreach (var panel in roots) panel.isDisposed = true;
            var nextRoot = new FairyGUI.GRoot(); var next = new FairyGUI.GMovieClip(); nextRoot.AddChild(next);
            FairyGUI.GRoot.SetExisting(nextRoot);
            for (int frame = 0; frame < 900; frame++) bridge.Tick(3 + frame * .01f);
            Equal(1, bridge.TrackedAnimations); Equal(0, bridge.ExternalRoots);
            var newPanel = new FairyGUI.GComponent(); bridge.RegisterExternalRoot(newPanel); Equal(1, bridge.ExternalRoots);
            if (next.ignoreEngineTimeScale) throw new Exception("New gear blocked by disposed cache entries");
            bridge.Restore(); if (!next.ignoreEngineTimeScale) throw new Exception("Live gear not restored");
        });
    }
}
