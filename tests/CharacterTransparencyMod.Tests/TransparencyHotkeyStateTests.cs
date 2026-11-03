using Xunit;

namespace CharacterTransparencyMod.Tests
{
    public sealed class TransparencyHotkeyStateTests
    {
        [Fact]
        public void HoldMode_ActivatesWhileHeld_AndRestoresOnRelease()
        {
            var state = new TransparencyHotkeyState();

            // Default holdSeconds = 0: activates immediately when pressed down
            Assert.True(state.Update(true, 10f, holdMode: true, holdSeconds: 0f));
            Assert.True(state.Active);

            // Stays active while holding
            Assert.True(state.Update(true, 11f, holdMode: true, holdSeconds: 0f));
            Assert.True(state.Active);

            // Deactivates immediately on release
            Assert.False(state.Update(false, 12f, holdMode: true, holdSeconds: 0f));
            Assert.False(state.Active);
        }

        [Fact]
        public void HoldMode_WithHoldSeconds_RequiresThresholdBeforeActivating()
        {
            var state = new TransparencyHotkeyState();

            // Before reaching threshold 0.3s
            Assert.False(state.Update(true, 20f, holdMode: true, holdSeconds: 0.3f));
            Assert.False(state.Update(true, 20.2f, holdMode: true, holdSeconds: 0.3f));
            Assert.False(state.Active);

            // At or past threshold
            Assert.True(state.Update(true, 20.3f, holdMode: true, holdSeconds: 0.3f));
            Assert.True(state.Active);

            // Release restores
            Assert.False(state.Update(false, 21f, holdMode: true, holdSeconds: 0.3f));
            Assert.False(state.Active);
        }

        [Fact]
        public void ToggleMode_FlipsOnPressEdgesOnly()
        {
            var state = new TransparencyHotkeyState();

            // First press edge: turns on
            Assert.True(state.Update(true, 30f, holdMode: false, holdSeconds: 0f));
            Assert.True(state.Active);

            // Still held down: remains on, does not toggle again
            Assert.True(state.Update(true, 31f, holdMode: false, holdSeconds: 0f));
            Assert.True(state.Active);

            // Key released: remains on
            Assert.True(state.Update(false, 32f, holdMode: false, holdSeconds: 0f));
            Assert.True(state.Active);

            // Second press edge: turns off
            Assert.False(state.Update(true, 33f, holdMode: false, holdSeconds: 0f));
            Assert.False(state.Active);

            // Key released: remains off
            Assert.False(state.Update(false, 34f, holdMode: false, holdSeconds: 0f));
            Assert.False(state.Active);
        }

        [Fact]
        public void Reset_ClearsActiveAndEdgeState()
        {
            var state = new TransparencyHotkeyState();
            state.Update(true, 40f, holdMode: false, holdSeconds: 0f);
            Assert.True(state.Active);

            state.Reset(keyDown: true, now: 40.5f);
            Assert.False(state.Active);

            // After reset with keyDown=true, holding the key should not re-trigger toggle
            Assert.False(state.Update(true, 41f, holdMode: false, holdSeconds: 0f));
            Assert.False(state.Update(false, 42f, holdMode: false, holdSeconds: 0f));

            // Fresh press triggers toggle
            Assert.True(state.Update(true, 43f, holdMode: false, holdSeconds: 0f));
            Assert.True(state.Active);
        }
    }
}
