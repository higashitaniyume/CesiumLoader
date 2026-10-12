using System.Collections.Generic;

namespace AutoThanksMod
{
    // Pure decision logic. The caller supplies real UTC ticks and reserves each attempt before its RPC.
    internal sealed class LikePolicy
    {
        internal const int DefaultIntervalMilliseconds = 500;
        private const long TicksPerMillisecond = 10000;
        private readonly HashSet<long> _attemptedPlayers = new HashSet<long>();
        private long _lastAttempt;
        private bool _hasAttempt;

        internal static int ClampIntervalMilliseconds(int intervalMilliseconds)
        {
            if (intervalMilliseconds < 100) return 100;
            if (intervalMilliseconds > 30000) return 30000;
            return intervalMilliseconds;
        }

        internal bool HasAttempted(long playerId) { return _attemptedPlayers.Contains(playerId); }

        internal bool CanAttempt(long playerId, long nowTicks,
            int intervalMilliseconds = DefaultIntervalMilliseconds)
        {
            if (playerId == 0 || _attemptedPlayers.Contains(playerId)) return false;
            return !_hasAttempt || nowTicks - _lastAttempt >=
                ClampIntervalMilliseconds(intervalMilliseconds) * TicksPerMillisecond;
        }

        // Record the actual attempt time, not a scheduled deadline: a stall earns no catch-up sends.
        // Reserve before sending so exceptions and failed responses cannot automatically retry a player.
        internal void MarkAttempt(long playerId, long nowTicks)
        {
            _attemptedPlayers.Add(playerId);
            _lastAttempt = nowTicks;
            _hasAttempt = true;
        }

        internal void Reset()
        {
            _attemptedPlayers.Clear();
            _lastAttempt = 0;
            _hasAttempt = false;
        }
    }
}
