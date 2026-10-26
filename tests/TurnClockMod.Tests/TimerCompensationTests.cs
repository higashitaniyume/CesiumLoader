using System;
using System.Collections.Generic;
using TurnClockMod;
using Xunit;

namespace TurnClockMod.Tests
{
    /// <summary>
    /// 补偿核心的离线用例: 用"假的 Unity Timer"重放游戏循环 ——
    /// 每帧游戏先按倍率推进 elapsed(到点即完成), 然后本 mod 修正。
    /// </summary>
    public class TimerCompensationTests
    {
        private const double Dt = 1.0 / 60.0;

        // ---------- 假的计时器(成员名与 Unity Timer 一致) ----------

        private sealed class FakeTimer
        {
            public float elapsedTime;
            public float duration;
        }

        private sealed class PropertyTimer
        {
            public float elapsedTime { get; private set; }
            public float duration;
        }

        private sealed class ConstantTimer
        {
            public float duration = 30f;
            public float loopInterval = -1f;
        }

        private sealed class GrowingTimer
        {
            public float duration = 30f;
            public float charge;
        }

        /// <summary>只读属性排在前面, 真正的字段排在后面(候选表要跳过只读的那个)。</summary>
        private sealed class MixedTimer
        {
            public float duration = 30f;
            public float _elapsedTime = 0f;
            public float elapsed = 0f;

            public float elapsedTime { get { return _elapsedTime; } }
        }

        // ---------- 工具 ----------

        private static TimerMember Elapsed(Type type)
        {
            return TimerMemberProbe.Find(type, TimerMemberProbe.ElapsedNames);
        }

        private static float Read(object timer, TimerMember member)
        {
            float value;
            member.TryRead(timer, out value);
            return value;
        }

        private static void Advance(object timer, TimerMember member, float delta)
        {
            member.TryWrite(timer, Read(timer, member) + delta);
        }

        /// <summary>按 <paramref name="speedAt"/>(墙钟秒) 重放一局, 返回计时器走完时的墙钟秒。</summary>
        private static double Simulate(object timer, float duration, Func<double, double> speedAt,
            TimerMember member, int maxFrames = 60 * 300)
        {
            var compensator = new TimerCompensator();
            double wall = 0.0;

            for (int frame = 0; frame < maxFrames; frame++)
            {
                double speed = speedAt(wall);

                // 游戏这一帧的推进(之后立刻判完成, 与 OperationTimer 的回调时机一致)
                Advance(timer, member, (float)(speed * Dt));
                wall += Dt;
                if (Read(timer, member) >= duration) return wall;

                compensator.TickElapsed(timer, Read(timer, member), duration, speed, member);
            }

            return double.PositiveInfinity;
        }

        // ---------- 核心: 2 倍速下窗口仍然走满 30 秒 ----------

        [Fact]
        public void AtDoubleSpeed_WindowStillLastsFullDuration()
        {
            var timer = new FakeTimer { duration = 30f };
            TimerMember member = Elapsed(typeof(FakeTimer));

            Assert.NotNull(member);
            Assert.Equal("elapsedTime", member.Name);

            double wall = Simulate(timer, timer.duration, _ => 2.0, member);

            // 补偿后 elapsed 与墙钟同步: 判定发生在"游戏又推进一帧"之后, 所以略早于 30s
            Assert.InRange(wall, 29.5, 30.6);
        }

        [Fact]
        public void AtDoubleSpeed_WithoutCompensation_FinishesInHalfTheTime()
        {
            // 对照组: 证明用例本身能观察到"2 倍速只剩 15 秒"这个现象
            var timer = new FakeTimer { duration = 30f };
            TimerMember member = Elapsed(typeof(FakeTimer));

            double wall = 0.0;
            for (int frame = 0; frame < 60 * 60; frame++)
            {
                Advance(timer, member, (float)(2.0 * Dt));
                wall += Dt;
                if (Read(timer, member) >= timer.duration) break;
            }

            Assert.InRange(wall, 14.5, 15.1);
        }

        [Fact]
        public void AtTripleSpeed_WindowStillLastsFullDuration()
        {
            var timer = new FakeTimer { duration = 30f };
            TimerMember member = Elapsed(typeof(FakeTimer));

            double wall = Simulate(timer, timer.duration, _ => 3.0, member);

            Assert.InRange(wall, 29.5, 30.6);
        }

        [Fact]
        public void AtNormalSpeed_DoesNotWriteAndKeepsProgress()
        {
            var timer = new FakeTimer { duration = 30f };
            TimerMember member = Elapsed(typeof(FakeTimer));

            var compensator = new TimerCompensator();
            int writes = 0;
            for (int frame = 0; frame < 600; frame++)
            {
                Advance(timer, member, (float)Dt);
                if (compensator.TickElapsed(timer, Read(timer, member), timer.duration, 1.0, member)) writes++;
            }

            Assert.Equal(0, writes);
            Assert.InRange((double)Read(timer, member), 9.9, 10.1);
        }

        [Fact]
        public void FirstSight_OnlyRecordsBaseline()
        {
            var timer = new FakeTimer { elapsedTime = 1f, duration = 30f };
            TimerMember member = Elapsed(typeof(FakeTimer));
            var compensator = new TimerCompensator();

            Assert.False(compensator.TickElapsed(timer, timer.elapsedTime, timer.duration, 2.0, member));
            Assert.Equal(1f, timer.elapsedTime);
            Assert.Equal(1, compensator.Count);
        }

        [Fact]
        public void PausedTimer_IsNotPushedForward()
        {
            var timer = new FakeTimer { duration = 30f };
            TimerMember member = Elapsed(typeof(FakeTimer));
            var compensator = new TimerCompensator();

            Advance(timer, member, 2f);
            compensator.TickElapsed(timer, Read(timer, member), timer.duration, 2.0, member);

            float before = Read(timer, member);
            for (int frame = 0; frame < 30; frame++)
            {
                // 游戏没推进(计时器暂停): 不能写, 否则恢复时会往前跳
                Assert.False(compensator.TickElapsed(timer, Read(timer, member), timer.duration, 2.0, member));
            }

            Assert.InRange((double)Read(timer, member), before - 0.001, before + 0.001);
        }

        [Fact]
        public void NeverWritesAheadOfObserved()
        {
            var timer = new FakeTimer { duration = 30f };
            TimerMember member = Elapsed(typeof(FakeTimer));
            var compensator = new TimerCompensator();

            for (int frame = 0; frame < 300; frame++)
            {
                Advance(timer, member, (float)(2.0 * Dt));
                float observed = Read(timer, member);
                if (compensator.TickElapsed(timer, observed, timer.duration, 2.0, member))
                    Assert.True(Read(timer, member) < observed);
            }
        }

        [Fact]
        public void SpeedChangeMidWindow_ContinuesFromCurrentPosition()
        {
            var timer = new FakeTimer { duration = 30f };
            TimerMember member = Elapsed(typeof(FakeTimer));

            // 前 10 秒 2 倍速, 之后回到 1 倍速: 剩下的时间按 1 倍速走完, 总时长仍是 30 秒
            double wall = Simulate(timer, timer.duration, t => t < 10.0 ? 2.0 : 1.0, member);

            Assert.InRange(wall, 29.5, 30.6);
        }

        // ---------- 成员探测 ----------

        [Fact]
        public void PrivateSetterProperty_IsWritable()
        {
            var timer = new PropertyTimer { duration = 30f };
            TimerMember member = Elapsed(typeof(PropertyTimer));

            Assert.NotNull(member);
            Assert.True(member.CanWrite);

            double wall = Simulate(timer, timer.duration, _ => 2.0, member);
            Assert.InRange(wall, 29.5, 30.6);
        }

        [Fact]
        public void Candidates_FallBackFromReadOnlyPropertyToField()
        {
            var timer = new MixedTimer();
            var candidates = TimerMemberProbe.FindAll(typeof(MixedTimer), TimerMemberProbe.ElapsedNames);

            Assert.NotEmpty(candidates);

            // 只读属性排在最前, 写一次失败后由候选表换下一个
            Assert.Equal("elapsedTime", candidates[0].Name);
            Assert.False(candidates[0].TryWrite(timer, 1f));
            Assert.True(candidates[0].IsBroken);

            Assert.True(candidates.Count > 1);
            Assert.Equal("_elapsedTime", candidates[1].Name);
            Assert.True(candidates[1].TryWrite(timer, 2f));
            Assert.Equal(2f, timer._elapsedTime);
        }

        [Fact]
        public void Describe_ListsFloatMembersWithValues()
        {
            string text = TimerMemberProbe.Describe(typeof(FakeTimer), new FakeTimer { elapsedTime = 1.5f, duration = 30f });

            Assert.Contains("elapsedTime", text);
            Assert.Contains("duration", text);
            Assert.Contains("1.5", text);
        }

        // ---------- 兜底认领 ----------

        [Fact]
        public void AutoPicker_ClaimsTheOnlyIncreasingField()
        {
            var picker = new IncreasingFieldPicker();
            var timer = new GrowingTimer();

            TimerMember picked = null;
            for (int frame = 0; frame < IncreasingFieldPicker.RequiredSamples; frame++)
            {
                timer.charge += 1f;
                picked = picker.Sample(typeof(GrowingTimer), timer);
            }

            Assert.NotNull(picked);
            Assert.Equal("charge", picked.Name);
        }

        [Fact]
        public void AutoPicker_GivesUpWhenNothingGrows()
        {
            var picker = new IncreasingFieldPicker();
            var timer = new ConstantTimer();

            TimerMember picked = null;
            for (int frame = 0; frame < IncreasingFieldPicker.RequiredSamples; frame++)
                picked = picker.Sample(typeof(ConstantTimer), timer);

            Assert.Null(picked);
            Assert.True(picker.GaveUp);
        }

        [Fact]
        public void Prune_DropsTimersThatAreGone()
        {
            var first = new FakeTimer { duration = 30f };
            var second = new FakeTimer { duration = 30f };
            TimerMember member = Elapsed(typeof(FakeTimer));
            var compensator = new TimerCompensator();

            compensator.TickElapsed(first, 0f, 30f, 2.0, member);
            compensator.TickElapsed(second, 0f, 30f, 2.0, member);
            Assert.Equal(2, compensator.Count);

            Assert.Equal(1, compensator.Prune(new List<object> { first }));
            Assert.Equal(1, compensator.Count);

            compensator.Clear();
            Assert.Equal(0, compensator.Count);
        }

        // ---------- start 模式: 与游戏实际用的 Unity Timer 一致(已用时长 = 时钟 - 起点) ----------

        /// <summary>成员名照抄真机日志里的 UnityTimer.Timer。</summary>
        private sealed class StartBasedTimer
        {
            public float _startTime = 0f;
            public float _lastUpdateTime;
            public float _delay = 0f;
            public float _loopDuration = -1f;
            public float _nextLoopTime = -1f;
            public float duration = 30f;

            public float Elapsed { get { return _lastUpdateTime - _startTime; } }
        }

        private static TimerMember StartOf(Type type)
        {
            return TimerMemberProbe.Find(type, TimerMemberProbe.StartNames);
        }

        /// <summary>重放一局: 游戏每帧推进时钟并刷新 _lastUpdateTime, 到点即完成; 然后本 mod 推后起点。</summary>
        private static double SimulateStart(StartBasedTimer timer, Func<double, double> speedAt,
            TimerMember startMember, bool compensate = true, int maxFrames = 60 * 300)
        {
            var compensator = new TimerCompensator();
            double wall = 0.0;
            float clock = timer._lastUpdateTime;

            for (int frame = 0; frame < maxFrames; frame++)
            {
                double speed = speedAt(wall);

                clock += (float)(speed * Dt);        // 游戏时钟推进(被变速放大)
                timer._lastUpdateTime = clock;       // Timer 每帧刷新
                wall += Dt;

                if (timer.Elapsed >= timer.duration) return wall;

                if (compensate)
                    compensator.TickStart(timer, timer._lastUpdateTime, timer._startTime, speed, startMember);
            }

            return double.PositiveInfinity;
        }

        [Fact]
        public void StartMode_KeepsWindowAtFullDurationUnderDoubleSpeed()
        {
            var timer = new StartBasedTimer();
            TimerMember startMember = StartOf(typeof(StartBasedTimer));

            Assert.NotNull(startMember);
            Assert.Equal("_startTime", startMember.Name);

            double wall = SimulateStart(timer, _ => 2.0, startMember);

            Assert.InRange(wall, 29.5, 30.6);
        }

        [Fact]
        public void StartMode_WithoutCompensation_FinishesInHalfTheTime()
        {
            var timer = new StartBasedTimer();
            TimerMember startMember = StartOf(typeof(StartBasedTimer));

            double wall = SimulateStart(timer, _ => 2.0, startMember, compensate: false);

            Assert.InRange(wall, 14.5, 15.1);
        }

        [Fact]
        public void StartMode_AtNormalSpeed_WritesNothing()
        {
            var timer = new StartBasedTimer();
            TimerMember startMember = StartOf(typeof(StartBasedTimer));
            var compensator = new TimerCompensator();

            float startBefore = timer._startTime;
            int writes = 0;

            float clock = 0f;
            for (int frame = 0; frame < 600; frame++)
            {
                clock += (float)Dt;
                timer._lastUpdateTime = clock;
                if (compensator.TickStart(timer, clock, timer._startTime, 1.0, startMember)) writes++;
            }

            Assert.Equal(0, writes);
            Assert.Equal(startBefore, timer._startTime);
        }

        [Fact]
        public void StartMode_PausedTimer_IsNotPushedForward()
        {
            var timer = new StartBasedTimer();
            TimerMember startMember = StartOf(typeof(StartBasedTimer));
            var compensator = new TimerCompensator();

            // 先正常跑几帧, 让基准建立起来
            float clock = 0f;
            for (int frame = 0; frame < 5; frame++)
            {
                clock += (float)(2.0 * Dt);
                timer._lastUpdateTime = clock;
                compensator.TickStart(timer, clock, timer._startTime, 2.0, startMember);
            }

            float startBefore = timer._startTime;
            for (int frame = 0; frame < 30; frame++)
            {
                // 时钟不动(计时器暂停): 起点不能被推
                Assert.False(compensator.TickStart(timer, clock, timer._startTime, 2.0, startMember));
            }
            Assert.Equal(startBefore, timer._startTime);
        }

        [Fact]
        public void StartMode_NeverPushesStartPastNow()
        {
            var timer = new StartBasedTimer();
            TimerMember startMember = StartOf(typeof(StartBasedTimer));
            var compensator = new TimerCompensator();

            // 基准
            compensator.TickStart(timer, 0f, timer._startTime, 2.0, startMember);

            // 一次巨大的跳变: 推后量必须被夹住, 不能让已用时长变负
            compensator.TickStart(timer, 100f, timer._startTime, 2.0, startMember);

            Assert.True(timer._startTime < 100f);
            Assert.True(100f - timer._startTime >= 0.05f);
        }

        [Fact]
        public void StartMode_SpeedDropMidWindow_KeepsRemainingTime()
        {
            var timer = new StartBasedTimer();
            TimerMember startMember = StartOf(typeof(StartBasedTimer));

            // 前 10 秒 2 倍速, 之后关掉倍速回到 1.0x: 剩余时间按 1 倍速走完, 总共仍是 30 秒
            double wall = SimulateStart(timer, t => t < 10.0 ? 2.0 : 1.0, startMember);

            Assert.InRange(wall, 29.5, 30.6);
        }

        [Fact]
        public void StartMode_ConstantSpeedToggling_DoesNotDrift()
        {
            var timer = new StartBasedTimer();
            TimerMember startMember = StartOf(typeof(StartBasedTimer));

            // 每 0.3 秒在 1x 和 9x 之间来回切: 只要每帧按"实际时钟推进"补偿, 总时长不受倍率历史影响
            double wall = SimulateStart(timer, t => ((int)(t / 0.3)) % 2 == 0 ? 9.0 : 1.0, startMember);

            Assert.InRange(wall, 29.0, 31.0);
        }

        [Fact]
        public void Compensator_ReportsLastDeltaForSpeedMeasurement()
        {
            var timer = new StartBasedTimer();
            TimerMember startMember = StartOf(typeof(StartBasedTimer));
            var compensator = new TimerCompensator();

            compensator.TickStart(timer, 0f, timer._startTime, 2.0, startMember);
            Assert.Equal(0f, compensator.LastDelta);

            compensator.TickStart(timer, 0.5f, timer._startTime, 2.0, startMember);
            Assert.InRange(compensator.LastDelta, 0.49f, 0.51f);
        }
    }
}
