using System;

namespace ChatDurationMod
{
    /// <summary>
    /// 停留时长的取值规则。独立于 Unity / SDK，便于离线测试。
    /// </summary>
    public static class ChatDurationRules
    {
        /// <summary>默认停留 15 秒真实时间。</summary>
        public const int DefaultSeconds = 15;

        /// <summary>最短停留 1 秒。</summary>
        public const int MinSeconds = 1;

        /// <summary>最长停留 300 秒。</summary>
        public const int MaxSeconds = 300;

        /// <summary>
        /// 把配置值规整到 1–300 秒：非正数按默认 15 秒，超过上限按 300 秒。
        /// 返回是否与输入不同，供调用方决定是否提示。
        /// </summary>
        public static int NormalizeSeconds(int value)
        {
            if (value <= 0) return DefaultSeconds;
            if (value > MaxSeconds) return MaxSeconds;
            if (value < MinSeconds) return MinSeconds;
            return value;
        }

        /// <summary>按真实时间判断条目是否已经到点。</summary>
        public static bool IsDue(double firstSeen, double now, double durationSeconds)
        {
            if (durationSeconds <= 0) return true;
            return now - firstSeen >= durationSeconds;
        }
    }
}
