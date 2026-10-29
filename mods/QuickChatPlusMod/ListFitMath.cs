namespace QuickChatPlusMod
{
    /// <summary>列表高度与上移量，单位与 FairyGUI 的 UI 坐标一致。</summary>
    public struct ListFit
    {
        public float Height;
        public float ShiftUp;
    }

    /// <summary>
    /// 纯几何计算：把过高的快捷短语列表限制在屏幕内。
    /// 不引用 FairyGUI，便于离线单测。
    /// </summary>
    public static class ListFitMath
    {
        /// <param name="contentHeight">列表全部条目的总高度。</param>
        /// <param name="itemCount">条目数。</param>
        /// <param name="maxVisibleItems">最多同时显示几条；0 表示按屏幕可用高度尽量多。</param>
        /// <param name="topY">列表左上角在界面坐标中的 Y。</param>
        /// <param name="screenHeight">界面高度。</param>
        /// <param name="margin">与屏幕上下边缘保留的间距。</param>
        /// <param name="minVisibleHeight">至少保留的可视高度（通常两行）。</param>
        public static ListFit Compute(float contentHeight, int itemCount, int maxVisibleItems,
            float topY, float screenHeight, float margin, float minVisibleHeight)
        {
            var fit = new ListFit { Height = contentHeight };
            if (contentHeight <= 0f || itemCount <= 0 || screenHeight <= 0f) return fit;
            if (margin < 0f) margin = 0f;

            float itemHeight = contentHeight / itemCount;
            float desired = maxVisibleItems > 0 && itemCount > maxVisibleItems
                ? itemHeight * maxVisibleItems
                : contentHeight;
            if (desired > contentHeight) desired = contentHeight;

            float available = screenHeight - margin - topY;
            float shift = 0f;
            if (available < desired)
            {
                float limit = topY - margin;
                if (limit > 0f) shift = System.Math.Min(desired - available, limit);
                if (shift > 0f) available += shift;
            }

            float height = System.Math.Min(desired, available);
            float screenLimit = screenHeight - margin * 2f;
            if (height > screenLimit) height = screenLimit;
            if (height < itemHeight) height = System.Math.Min(itemHeight, contentHeight);
            if (height < minVisibleHeight && minVisibleHeight < desired) height = System.Math.Min(minVisibleHeight, desired);
            if (height < 0f) height = 0f;

            fit.Height = height;
            fit.ShiftUp = shift;
            return fit;
        }
    }
}
