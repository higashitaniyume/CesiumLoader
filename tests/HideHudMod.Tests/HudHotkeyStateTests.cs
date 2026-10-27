using Xunit;

namespace HideHudMod.Tests
{
    public sealed class HudHotkeyStateTests
    {
        [Fact]
        public void Hold_HidesAfterThreshold_AndShowsOnRelease()
        {
            var state = new HudHotkeyState();

            Assert.False(state.Update(true, 10f, HudPressMode.Hold, 0.35f));
            Assert.False(state.Update(true, 10.34f, HudPressMode.Hold, 0.35f));
            Assert.True(state.Update(true, 10.35f, HudPressMode.Hold, 0.35f));
            Assert.True(state.Hidden);

            Assert.False(state.Update(false, 11f, HudPressMode.Hold, 0.35f));
            Assert.False(state.Hidden);
        }

        [Fact]
        public void Hold_QuickTapDoesNotHide()
        {
            var state = new HudHotkeyState();

            Assert.False(state.Update(true, 20f, HudPressMode.Hold, 0.35f));
            Assert.False(state.Update(false, 20.1f, HudPressMode.Hold, 0.35f));
            Assert.False(state.Hidden);
        }

        [Fact]
        public void Toggle_FlipsOnPressEdgesOnly()
        {
            var state = new HudHotkeyState();

            Assert.True(state.Update(true, 30f, HudPressMode.Toggle, 0.35f));
            Assert.True(state.Update(true, 31f, HudPressMode.Toggle, 0.35f));
            Assert.True(state.Hidden);

            Assert.True(state.Update(false, 32f, HudPressMode.Toggle, 0.35f));
            Assert.True(state.Hidden);

            Assert.False(state.Update(true, 33f, HudPressMode.Toggle, 0.35f));
            Assert.False(state.Hidden);
        }

        [Fact]
        public void Reset_ShowsAndClearsEdgeState()
        {
            var state = new HudHotkeyState();
            state.Update(true, 40f, HudPressMode.Toggle, 0.35f);

            state.Reset(keyDown: true, now: 40.5f);

            Assert.False(state.Hidden);
            // 重置后仍按着不会补触发一次切换，必须先松开再按。
            Assert.False(state.Update(true, 41f, HudPressMode.Toggle, 0.35f));
            Assert.False(state.Update(false, 42f, HudPressMode.Toggle, 0.35f));
            Assert.True(state.Update(true, 43f, HudPressMode.Toggle, 0.35f));
        }
    }
}
