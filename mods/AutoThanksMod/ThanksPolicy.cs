namespace AutoThanksMod
{
    // Pure decision logic: no Unity, RPC or wall-clock reads. The caller supplies real UTC ticks.
    internal sealed class ThanksPolicy
    {
        internal const long TicksPerSecond = 10000000;
        private long _lastAttempt;
        private bool _hasAttempt;

        internal bool TryGetChatId(long sender, long recipient, long accountId, int triggerType,
            bool sameTeam, bool blocked, bool healingEnabled, bool cardsEnabled, bool transfersEnabled,
            long receivedTicks, long nowTicks, int cooldownSeconds, out int chatId)
        {
            chatId = 0;
            if (accountId == 0 || recipient != accountId || sender == 0 || sender == accountId ||
                !sameTeam || blocked) return false;
            long age = nowTicks - receivedTicks;
            if (age < 0 || age > 5 * TicksPerSecond) return false;
            int candidate;
            if (triggerType == 3 && healingEnabled) candidate = 50001; // TriggerPhrase.CureFriend
            else if (triggerType == 2 && cardsEnabled) candidate = 50002; // TriggerPhrase.GiveCard
            else if (triggerType == 1 && transfersEnabled) candidate = 50000; // TriggerPhrase.TransferGold
            else return false;
            if (cooldownSeconds < 0) cooldownSeconds = 0;
            if (cooldownSeconds > 30) cooldownSeconds = 30;
            if (_hasAttempt && nowTicks - _lastAttempt < cooldownSeconds * TicksPerSecond) return false;
            chatId = candidate;
            return true;
        }

        // Reserve before the RPC call: a failed send must not cause a rapid retry loop.
        internal void MarkAttempt(long nowTicks) { _lastAttempt = nowTicks; _hasAttempt = true; }
        internal void Reset() { _hasAttempt = false; _lastAttempt = 0; }
    }
}
