using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SpeedHackMod
{
    /// <summary>
    /// Keep the normal-speed fixed-update frequency while the native clock is accelerated.
    /// This changes simulation precision per game second, not the clock multiplier.
    /// Accessors are injected so the controller's ownership and restoration can be tested
    /// without starting Unity. All calls in the game remain on the main thread.
    /// </summary>
    internal sealed class FixedStepBudget
    {
        private readonly Func<float> _readStep;
        private readonly Func<float> _readMaximum;
        private readonly Action<float> _writeStep;
        private float _baseline;
        private float _applied;
        private bool _ownsStep;

        internal FixedStepBudget(Func<float> readStep, Func<float> readMaximum, Action<float> writeStep)
        {
            _readStep = readStep;
            _readMaximum = readMaximum;
            _writeStep = writeStep;
        }

        internal void Synchronize(double actualSpeed)
        {
            if (double.IsNaN(actualSpeed) || double.IsInfinity(actualSpeed) || actualSpeed < 1.0)
                return;
            if (actualSpeed <= 1.000001)
            {
                Restore();
                return;
            }

            float current = _readStep();
            float maximum = _readMaximum();
            if (!Valid(current) || !Valid(maximum)) return;

            if (!_ownsStep || !Same(current, _applied))
            {
                // A scene or another owner has replaced our value. Treat that latest
                // value as the game's new baseline rather than restoring an obsolete one.
                _baseline = current;
                _ownsStep = false;
            }

            float desired = (float)Math.Min(_baseline * actualSpeed, maximum);
            if (!Valid(desired) || Same(current, desired)) return;
            _writeStep(desired);
            _applied = desired;
            _ownsStep = true;
        }

        internal void Restore()
        {
            if (!_ownsStep) return;
            // Never undo a value written by the game or another mod after our last write.
            if (Same(_readStep(), _applied)) _writeStep(_baseline);
            _ownsStep = false;
        }

        private static bool Valid(float value) => value > 0f && !float.IsInfinity(value) && !float.IsNaN(value);
        private static bool Same(float a, float b) => Math.Abs(a - b) <= Math.Max(0.0000001f, Math.Abs(b) * 0.00001f);
    }

    /// <summary>
    /// Separate ECalls from their caller's catch block. HybridCLR can reject a Unity
    /// internal call while compiling the called method, before that method's own catch.
    /// These tiny non-inlined methods keep that failure catchable by the controller.
    /// </summary>
    internal static class UnityFixedStepAccess
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float ReadStep() => Time.fixedDeltaTime;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float ReadMaximum() => Time.maximumDeltaTime;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void WriteStep(float value) { Time.fixedDeltaTime = value; }
    }
}
