using System;
using GameLogic;
using Tools;
using UnityEngine;
using Xunit;

namespace TurnClockMod.Tests
{
    [CollectionDefinition("TurnClock controller", DisableParallelization = true)]
    public sealed class TurnClockControllerCollection { }

    [Collection("TurnClock controller")]
    public sealed class TurnClockControllerTests : IDisposable
    {
        private double _now = 100;
        private double? _native = 1;
        private int _nativeReads;

        private sealed class Timer
        {
            private bool _realtime;
            public int RateReads;
            public bool usesRealTime { get { RateReads++; return _realtime; } set { _realtime = value; } }
            public bool isDone { get; set; }
            public float _startTime = 0, _lastUpdateTime = .1f, duration = 30;
            public float Used => _lastUpdateTime - _startTime;
        }
        private sealed class RoomHeroPanel { public Timer timeLimiter; }

        public TurnClockControllerTests() { Reset(); }
        public void Dispose() { Reset(); }
        private static void Reset()
        {
            Time.Reset(); OperationTimer.Reset();
            SimpleSingletonProvider<UI.UIManager>.inst = null;
            SimpleSingletonProvider<GameLogicManager>.inst = null;
        }
        private TurnClock Create(bool logWindow = false)
        {
            var mod = new TurnClock();
            mod.Config.Set("enabled", true); mod.Config.Set("logWindow", logWindow);
            mod.ReadWallSeconds = () => _now;
            mod.ReadNativeSpeed = () => { _nativeReads++; return _native; };
            mod.OnInitialize();
            return mod;
        }
        private void Tick(TurnClock mod, Timer[] timers, float delta = .08f, double realDelta = .02)
        {
            _now += realDelta;
            foreach (var timer in timers) timer._lastUpdateTime += timer.usesRealTime ? (float)(realDelta * (_native ?? 1)) : delta;
            mod.OnLateUpdate();
        }

        [Fact]
        public void MixedOperationAndUiTimersShareOneFrameSnapshot()
        {
            var timers = new Timer[12];
            for (int i = 0; i < 11; i++) { timers[i] = new Timer(); OperationTimer.Set(i, timers[i]); }
            timers[11] = new Timer { usesRealTime = true };
            var ui = new UI.UIManager(); ui.SetPanel(1, new RoomHeroPanel { timeLimiter = timers[11] });
            SimpleSingletonProvider<UI.UIManager>.inst = ui;
            var mod = Create(); mod.OnLateUpdate();
            Assert.Equal(1, _nativeReads);
            Assert.Equal(1, Time.DeltaReads); Assert.Equal(1, Time.UnscaledReads); Assert.Equal(0, Time.ScaleReads);
            for (int i = 0; i < 100; i++) Tick(mod, timers);
            foreach (var timer in timers) Assert.InRange(timer.Used, 2.099f, 2.102f);
            Assert.Equal(101, Time.DeltaReads); Assert.Equal(101, Time.UnscaledReads);
            Assert.InRange(_nativeReads, 9, 11);
            Assert.Empty(mod.Log.Warnings); mod.OnUnload();
        }

        [Fact]
        public void CurrentFrameRatioNotANewlyWrittenTimeScaleControlsCompensation()
        {
            var timer = new Timer(); OperationTimer.Set(1, timer);
            var mod = Create(); mod.OnLateUpdate();
            Time.Scale = 20; // written mid-frame; this frame still advanced at 4x
            for (int i = 0; i < 100; i++) Tick(mod, new[] { timer });
            Assert.InRange(timer.Used, 2.099f, 2.102f);
            Assert.Equal(0, Time.ScaleReads); mod.OnUnload();
        }

        [Fact]
        public void MaximumDeltaTimeCapDoesNotOvercompensateOrForceReadsEveryFrame()
        {
            Time.Delta = .1f; Time.UnscaledDelta = .05f; Time.Scale = 4;
            var timer = new Timer(); OperationTimer.Set(1, timer);
            var mod = Create(); mod.OnLateUpdate();
            for (int i = 0; i < 20; i++) Tick(mod, new[] { timer }, .1f, .05);
            Assert.InRange(timer.Used, 1.099f, 1.102f);
            Assert.InRange(_nativeReads, 4, 6); mod.OnUnload();
        }

        [Fact]
        public void MissingNativeStateObservesWithoutWritingAndResumesWithoutCatchup()
        {
            var timer = new Timer(); OperationTimer.Set(1, timer);
            var mod = Create(); mod.OnLateUpdate();
            _native = null;
            _now += .21;
            timer._lastUpdateTime += .08f; mod.OnLateUpdate();
            Assert.Equal(0, timer._startTime);
            for (int i = 0; i < 20; i++) Tick(mod, new[] { timer });
            Assert.Equal(0, timer._startTime);
            Assert.Single(mod.Log.Warnings);
            Assert.Equal(1, Time.DeltaReads); // unknown native state needs no Unity calls
            float before = timer.Used;
            _native = 1; _now += .21;
            timer._lastUpdateTime += .08f; mod.OnLateUpdate();
            Assert.InRange(timer.Used - before, .0199f, .0201f);
            Assert.Contains(mod.Log.Messages, message => message.Contains("原生时钟状态已恢复"));
            mod.OnUnload();
        }

        [Fact]
        public void UnsupportedUnityCallsFailOnceAndCanRetryAfterConfigReload()
        {
            Time.FailDeltas = Time.FailScale = true;
            _native = 4;
            var timer = new Timer(); OperationTimer.Set(1, timer);
            var mod = Create(); mod.OnLateUpdate();
            for (int i = 0; i < 100; i++) Tick(mod, new[] { timer });
            Assert.Equal(1, Time.DeltaReads); Assert.Equal(1, Time.ScaleReads);
            Assert.Equal(2, mod.Log.Warnings.Count);
            Assert.InRange(timer.Used, 2.099f, 2.102f);
            Time.FailDeltas = Time.FailScale = false; _native = 1;
            mod.OnConfigChanged(); mod.OnLateUpdate();
            Assert.Equal(2, Time.DeltaReads); Assert.Equal(1, Time.UnscaledReads);
            mod.OnUnload();
        }

        [Fact]
        public void IdleAndLoadingDoNotSampleClockApis()
        {
            var mod = Create();
            for (int i = 0; i < 100; i++) { _now += .02; mod.OnLateUpdate(); }
            Assert.Equal(0, _nativeReads); Assert.Equal(0, Time.DeltaReads);
            var timer = new Timer(); OperationTimer.Set(1, timer);
            SimpleSingletonProvider<GameLogicManager>.inst = new GameLogicManager
            {
                room = new RoomLogic { roomController = new RoomController { roomStateType = RoomStateType.RUNNING } },
                battle = new BattleLogic { clientFinishReady = false }
            };
            for (int i = 0; i < 100; i++) Tick(mod, new[] { timer });
            Assert.Equal(0, _nativeReads); Assert.Equal(0, Time.DeltaReads);
            mod.OnUnload();
        }

        [Fact]
        public void PersistentMeasuredMismatchRemainsWithinTheNativeReadBudget()
        {
            Time.Delta = Time.UnscaledDelta = .001f; Time.Scale = 1;
            var timer = new Timer(); OperationTimer.Set(1, timer);
            var mod = Create(); mod.OnLateUpdate();
            // Deliberately mismatched clock source to exercise the emergency path.
            for (int i = 0; i < 1000; i++) Tick(mod, new[] { timer }, .01f, .001);
            Assert.InRange(_nativeReads, 19, 21);
            mod.OnUnload();
        }

        [Fact]
        public void AnExtraOnlyWindowRefreshesAfterIdleWithoutWaitingForRegularDeadline()
        {
            var ui = new UI.UIManager();
            ui.SetPanel(1, new RoomHeroPanel { timeLimiter = new Timer() });
            SimpleSingletonProvider<UI.UIManager>.inst = ui;
            var mod = Create(); mod.OnLateUpdate(); Assert.Equal(1, _nativeReads);
            ui.Clear(); _now += .01; mod.OnLateUpdate();
            var next = new Timer(); ui.SetPanel(1, new RoomHeroPanel { timeLimiter = next });
            _native = 4; _now += .06; Time.Delta = Time.UnscaledDelta = .02f;
            mod.OnLateUpdate(); Assert.Equal(2, _nativeReads);
            for (int i = 0; i < 20; i++) Tick(mod, new[] { next }, .02f, .005);
            Assert.InRange(next.Used, .1999f, .2001f);
            mod.OnUnload();
        }

        [Fact]
        public void PausePreservesScaledTimerButNotRealtimeTimerProgress()
        {
            var scaled = new Timer(); var realtime = new Timer { usesRealTime = true };
            OperationTimer.Set(1, scaled); OperationTimer.Set(2, realtime);
            var mod = Create(); mod.OnLateUpdate();
            Time.Scale = Time.Delta = 0;
            for (int i = 0; i < 20; i++) Tick(mod, new[] { scaled, realtime }, 0);
            Assert.Equal(.1f, scaled.Used, 5); Assert.Equal(.5f, realtime.Used, 5);
            Time.Scale = 4; Time.Delta = .02f; // this frame still used the old 1x rate
            Tick(mod, new[] { scaled, realtime }, .02f);
            Assert.Equal(.12f, scaled.Used, 5);
            Time.Delta = .08f;
            Tick(mod, new[] { scaled, realtime });
            Assert.Equal(.14f, scaled.Used, 5);
            mod.OnUnload();
        }

        [Fact]
        public void DuplicateTimerReferencesAreProcessedOnlyOncePerFrame()
        {
            var timer = new Timer();
            OperationTimer.Set(1, timer); OperationTimer.Set(2, timer); OperationTimer.Show(timer);
            var mod = Create(); mod.OnLateUpdate();
            Assert.Equal(1, timer.RateReads);
            mod.OnUnload();
        }

        /// <summary>没有结束标记的计时器类型不能让别的类型也失去"已结束"过滤。</summary>
        private sealed class PlainTimer
        {
            public float _startTime = 0f;
            public float _lastUpdateTime = .1f;
            public float duration = 30f;
        }

        [Fact]
        public void FinishedShowPlayerTimerDoesNotKeepTheWindowOpen()
        {
            var mine = new Timer();
            OperationTimer.Set(1, mine);
            // 游戏 Cancel 之后 ShowPlayerTimer 仍指向那个对象(字段从不置空)
            OperationTimer.Show(new Timer { isDone = true });

            var mod = Create(logWindow: true);
            mod.OnLateUpdate();
            for (int i = 0; i < 20; i++) Tick(mod, new[] { mine });

            OperationTimer.Clear();               // 我这一回合结束
            _now += .02; mod.OnLateUpdate();

            Assert.Contains(mod.Log.Messages, message => message.Contains("操作窗口结束"));

            // 只剩下"已结束"的 ShowPlayerTimer: 应当完全空闲, 不再采样倍率/读帧时钟
            int readsAfterIdle = _nativeReads;
            for (int i = 0; i < 50; i++) { _now += .02; mod.OnLateUpdate(); }
            Assert.Equal(readsAfterIdle, _nativeReads);
            mod.OnUnload();
        }

        [Fact]
        public void OtherPlayersCountdownIsCompensatedButDoesNotHoldMyWindowOpen()
        {
            var mine = new Timer();
            var other = new Timer();
            OperationTimer.Set(1, mine);
            OperationTimer.Show(other);

            var mod = Create(logWindow: true);
            mod.OnLateUpdate();
            for (int i = 0; i < 50; i++) Tick(mod, new[] { mine, other });

            OperationTimer.Clear();
            _now += .02; mod.OnLateUpdate();

            Assert.Contains(mod.Log.Messages, message => message.Contains("操作窗口结束"));
            // 别人回合的倒计时仍然按真实时间走(.1 起始 + 50 帧 × .02s 真实)
            Assert.InRange(other.Used, 1.099f, 1.102f);
            mod.OnUnload();
        }

        [Fact]
        public void FinishedOperationTimerStopsCountingAsTheWindow()
        {
            var mine = new Timer();
            OperationTimer.Set(1, mine);

            var mod = Create(logWindow: true);
            mod.OnLateUpdate();
            for (int i = 0; i < 20; i++) Tick(mod, new[] { mine });

            mine.isDone = true;                   // 已完成, 但游戏还没从字典里删掉
            _now += .02; mod.OnLateUpdate();

            Assert.Contains(mod.Log.Messages, message => message.Contains("操作窗口结束"));
            mod.OnUnload();
        }

        [Fact]
        public void FinishFlagsAreProbedPerTimerType()
        {
            OperationTimer.Set(1, new PlainTimer());

            var mod = Create(logWindow: true);
            mod.OnLateUpdate();                   // 先探到"没有结束标记"的类型

            OperationTimer.Clear();
            OperationTimer.Set(2, new Timer { isDone = true });   // 已完成但残留在字典里
            _now += .02; mod.OnLateUpdate();

            Assert.Contains(mod.Log.Messages, message => message.Contains("操作窗口结束"));
            mod.OnUnload();
        }
    }
}
