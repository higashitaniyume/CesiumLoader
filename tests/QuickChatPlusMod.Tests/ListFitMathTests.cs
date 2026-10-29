using QuickChatPlusMod;
using Xunit;

namespace QuickChatPlusMod.Tests
{
    public sealed class ListFitMathTests
    {
        private const float Margin = 20f;

        [Fact]
        public void CapsToMaxVisibleItems()
        {
            // 32 条、共 1280 高，单条约 40；最多显示 10 条 = 400。
            var fit = ListFitMath.Compute(1280f, 32, 10, topY: 100f, screenHeight: 1080f,
                margin: Margin, minVisibleHeight: 80f);

            Assert.Equal(400f, fit.Height, 3);
            Assert.Equal(0f, fit.ShiftUp, 3);
        }

        [Fact]
        public void ShiftsUpWhenListWouldOverflowBottom()
        {
            var fit = ListFitMath.Compute(1280f, 32, 10, topY: 900f, screenHeight: 1080f,
                margin: Margin, minVisibleHeight: 80f);

            Assert.Equal(400f, fit.Height, 3);
            Assert.Equal(240f, fit.ShiftUp, 3);
        }

        [Fact]
        public void NoShiftWhenThereIsEnoughRoom()
        {
            var fit = ListFitMath.Compute(1280f, 32, 10, topY: 200f, screenHeight: 1080f,
                margin: Margin, minVisibleHeight: 80f);

            Assert.Equal(0f, fit.ShiftUp, 3);
            Assert.Equal(400f, fit.Height, 3);
        }

        [Fact]
        public void ZeroMaxVisibleUsesScreenSpace()
        {
            var fit = ListFitMath.Compute(1280f, 32, 0, topY: 100f, screenHeight: 1080f,
                margin: Margin, minVisibleHeight: 80f);

            Assert.Equal(1040f, fit.Height, 3);
            Assert.Equal(80f, fit.ShiftUp, 3);
        }

        [Fact]
        public void ShortListIsLeftAlone()
        {
            var fit = ListFitMath.Compute(300f, 6, 10, topY: 200f, screenHeight: 1080f,
                margin: Margin, minVisibleHeight: 80f);

            Assert.Equal(300f, fit.Height, 3);
            Assert.Equal(0f, fit.ShiftUp, 3);
        }

        [Fact]
        public void ShiftIsLimitedByTopMargin()
        {
            // 列表已经贴近顶部，只能上移 10。
            var fit = ListFitMath.Compute(1280f, 32, 10, topY: 30f, screenHeight: 100f,
                margin: Margin, minVisibleHeight: 80f);

            Assert.Equal(10f, fit.ShiftUp, 3);
            Assert.True(fit.Height > 0f);
        }

        [Fact]
        public void NeverExceedsContentHeight()
        {
            var fit = ListFitMath.Compute(300f, 6, 40, topY: 100f, screenHeight: 1080f,
                margin: Margin, minVisibleHeight: 80f);

            Assert.Equal(300f, fit.Height, 3);
        }

        [Fact]
        public void KeepsAtLeastOneRow()
        {
            var fit = ListFitMath.Compute(1280f, 32, 10, topY: 900f, screenHeight: 950f,
                margin: Margin, minVisibleHeight: 80f);

            Assert.True(fit.Height >= 40f, "至少保留一行");
        }

        [Fact]
        public void EmptyInputIsSafe()
        {
            Assert.Equal(0f, ListFitMath.Compute(0f, 0, 10, 0f, 1080f, Margin, 0f).Height);
            // 没有条目时不做裁剪，保持传入的内容高度。
            Assert.Equal(100f, ListFitMath.Compute(100f, 0, 10, 0f, 1080f, Margin, 0f).Height);
        }
    }
}
