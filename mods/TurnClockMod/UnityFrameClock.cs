using System;

namespace TurnClockMod
{
    internal static class UnityFrameClock
    {
        /// <summary>
        /// The timer's current frame can still use the previous timeScale. The
        /// delta ratio also accounts for Unity's maximumDeltaTime cap at high speed.
        /// A zero scaled delta is a real pause, not a failed clock read.
        /// </summary>
        internal static double? Scale(float delta, float unscaledDelta)
        {
            if (float.IsNaN(delta) || float.IsInfinity(delta) || delta < 0 ||
                float.IsNaN(unscaledDelta) || float.IsInfinity(unscaledDelta) || unscaledDelta <= 0)
                return null;
            return (double)delta / unscaledDelta;
        }
    }
}
