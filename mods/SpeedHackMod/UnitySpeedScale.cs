using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SpeedHackMod
{
    /// <summary>
    /// Own only the timeScale multiplier we apply. Native clocks must already be
    /// confirmed at 1x by the controller before this class is used.
    /// Game pause and later writes from the game are respected. Accessors make this
    /// ownership protocol testable independently of Unity's internal calls.
    /// </summary>
    internal sealed class UnitySpeedScale
    {
        private readonly Func<float> _read;
        private readonly Action<float> _write;
        private float _baseline = 1f;
        private float _applied;
        private bool _ownsScale;

        internal UnitySpeedScale(Func<float> read, Action<float> write)
        {
            _read = read;
            _write = write;
        }

        internal void Synchronize(double speed)
        {
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed < 1.0 || speed > 100.0)
                throw new ArgumentOutOfRangeException(nameof(speed));
            if (speed <= 1.000001) { Restore(); return; }

            float current = _read();
            if (float.IsNaN(current) || float.IsInfinity(current) || current < 0f)
                throw new InvalidOperationException("Unity timeScale is invalid");
            if (current == 0f)
            {
                // Never unpause the game. Recapture the game's baseline after resume.
                _ownsScale = false;
                return;
            }
            if (!_ownsScale || !Same(current, _applied)) _baseline = current;
            float desired = (float)Math.Min(100.0, _baseline * speed);
            if (Same(current, desired)) return;
            _write(desired);
            _applied = desired;
            _ownsScale = true;
        }

        internal void Restore()
        {
            if (!_ownsScale) return;
            if (Same(_read(), _applied)) _write(_baseline);
            _ownsScale = false;
        }

        private static bool Same(float a, float b) => Math.Abs(a - b) <= Math.Max(0.000001f, Math.Abs(b) * 0.00001f);
    }

    internal static class UnityScaleAccess
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float Read() => Time.timeScale;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Write(float value) { Time.timeScale = value; }
    }
}
