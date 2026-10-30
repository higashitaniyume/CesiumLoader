using System.Globalization;

namespace UnlockFrameRateMod
{
    /// <summary>
    /// 追加到游戏帧率列表里的选项与文案(纯托管, 不碰 Unity / 游戏类型, 便于离线单测)。
    /// </summary>
    internal static class FrameRateOptions
    {
        /// <summary>不限帧率: 游戏直接把这个值写进 Application.targetFrameRate。</summary>
        internal const int Unlimited = -1;

        internal const int MinCustom = 1;
        internal const int MaxCustom = 1000;

        /// <summary>追加条目的刷新率 Id 起点(原生列表用 1–4)。</summary>
        internal const int RateIdBase = 100;

        /// <summary>追加文案的 STR Id 起点, 以及为此保留的区间宽度。</summary>
        internal const int DescriptionIdBase = 900000;
        internal const int DescriptionIdSpan = 10000;

        /// <summary>追加到列表末尾的帧率。顺序即列表顺序。</summary>
        internal static readonly int[] Values = { 144, 165, 240, 360, Unlimited };

        /// <summary>是否是本 mod 追加的文案 Id(用于识别/移除自己的条目)。</summary>
        internal static bool IsExtraDescriptionId(int id)
        {
            return id >= DescriptionIdBase && id < DescriptionIdBase + DescriptionIdSpan;
        }

        internal static bool IsValidValue(int value)
        {
            return value == Unlimited || (value >= MinCustom && value <= MaxCustom);
        }

        /// <summary>日志里的描述。</summary>
        internal static string Describe(int framesPerSecond)
        {
            if (framesPerSecond < 0) return "不限帧率";
            return framesPerSecond.ToString(CultureInfo.InvariantCulture) + " FPS";
        }

        // 列表文案: 与原生条目保持同样的“数字 + 帧”格式, 同时补齐其它语言。

        internal static string Simplified(int framesPerSecond)
        {
            return framesPerSecond < 0 ? "不限帧" : Num(framesPerSecond) + "帧";
        }

        internal static string Traditional(int framesPerSecond)
        {
            return framesPerSecond < 0 ? "不限幀" : Num(framesPerSecond) + "幀";
        }

        internal static string English(int framesPerSecond)
        {
            return framesPerSecond < 0 ? "Unlimited" : Num(framesPerSecond) + " FPS";
        }

        internal static string Japanese(int framesPerSecond)
        {
            return framesPerSecond < 0 ? "無制限" : Num(framesPerSecond) + " FPS";
        }

        internal static string Korean(int framesPerSecond)
        {
            return framesPerSecond < 0 ? "무제한" : Num(framesPerSecond) + " FPS";
        }

        private static string Num(int framesPerSecond)
        {
            return framesPerSecond.ToString(CultureInfo.InvariantCulture);
        }
    }
}
