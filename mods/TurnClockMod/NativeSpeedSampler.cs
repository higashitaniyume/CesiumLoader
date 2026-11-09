using System;

namespace TurnClockMod
{
    /// <summary>
    /// Regular polling and anomaly-triggered refreshes share the same hard budget.
    /// Unknown state is not a 1x acknowledgement, nor permission to reuse old speed.
    /// </summary>
    internal sealed class NativeSpeedSampler
    {
        internal const double MinReadInterval = 0.05;
        private double _nextRead = double.NegativeInfinity;
        private double _earliestRead = double.NegativeInfinity;
        private double _lastNow = double.NaN;
        private bool _refresh = true;
        private double? _speed;

        internal double? Sample(double now, double interval, Func<double?> read)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) return null;
            // UTC is deliberately used instead of hooked QPC. Rebase deadlines if
            // the system clock moves backward, rather than retaining a stale cache.
            if (!double.IsNaN(_lastNow) && now < _lastNow) Reset();
            _lastNow = now;
            if (now < _earliestRead || (!_refresh && now < _nextRead)) return _speed;

            interval = NormalizeInterval(interval);
            _earliestRead = now + MinReadInterval;
            _nextRead = now + interval;
            _refresh = false;
            double? value;
            try { value = read(); }
            catch { value = null; }
            _speed = value.HasValue && value.Value >= 1 && value.Value <= 100 ? value : null;
            return _speed;
        }

        internal void RequestRefresh() { _refresh = true; }

        internal void Reset()
        {
            _speed = null;
            _refresh = true;
            _nextRead = _earliestRead = double.NegativeInfinity;
            _lastNow = double.NaN;
        }

        internal static double NormalizeInterval(double interval)
        {
            if (double.IsNaN(interval) || double.IsInfinity(interval)) return 0.2;
            return Math.Max(MinReadInterval, Math.Min(5, interval));
        }
    }
}
