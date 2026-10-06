using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using CesiumLoader.SDK.Internals;

namespace CesiumLoader.SDK.Scheduling
{
    /// <summary>等待若干秒(使用实时时间, 不受 Time.timeScale 影响)。</summary>
    public sealed class RoutineWaitSeconds : CoroutineYield
    {
        private readonly float _seconds;
        private float _deadline = float.NaN;

        /// <summary>构造。</summary>
        public RoutineWaitSeconds(float seconds) { _seconds = seconds < 0f ? 0f : seconds; }

        /// <summary>剩余秒数。</summary>
        public float Remaining
        {
            get
            {
                if (float.IsNaN(_deadline)) return _seconds;
                float now = SafeNow();
                return Math.Max(0f, _deadline - now);
            }
        }

        /// <summary>是否仍需等待。</summary>
        public override bool KeepWaiting
        {
            get
            {
                if (float.IsNaN(_deadline)) _deadline = SafeNow() + _seconds;
                return SafeNow() < _deadline;
            }
        }

        private static float SafeNow()
        {
            return UnityCall.RealtimeSinceStartup();
        }
    }

    /// <summary>等待若干帧。</summary>
    public sealed class RoutineWaitFrames : CoroutineYield
    {
        private int _remaining;

        /// <summary>构造。</summary>
        public RoutineWaitFrames(int frames) { _remaining = frames < 0 ? 0 : frames; }

        /// <summary>剩余帧数。</summary>
        public int Remaining { get { return _remaining; } }

        /// <summary>是否仍需等待。</summary>
        public override bool KeepWaiting
        {
            get
            {
                if (_remaining <= 0) return false;
                _remaining--;
                return _remaining > 0;
            }
        }
    }

    /// <summary>等待直到条件为真。</summary>
    public sealed class RoutineWaitUntil : CoroutineYield
    {
        private readonly Func<bool> _predicate;

        /// <summary>构造。</summary>
        public RoutineWaitUntil(Func<bool> predicate) { _predicate = predicate; }

        /// <summary>是否仍需等待。</summary>
        public override bool KeepWaiting
        {
            get
            {
                if (_predicate == null) return false;
                try { return !_predicate(); }
                catch { return false; }
            }
        }
    }

    /// <summary>等待直到条件为假。</summary>
    public sealed class RoutineWaitWhile : CoroutineYield
    {
        private readonly Func<bool> _predicate;

        /// <summary>构造。</summary>
        public RoutineWaitWhile(Func<bool> predicate) { _predicate = predicate; }

        /// <summary>是否仍需等待。</summary>
        public override bool KeepWaiting
        {
            get
            {
                if (_predicate == null) return false;
                try { return _predicate(); }
                catch { return false; }
            }
        }
    }
}
