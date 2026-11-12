using FairyGUI;
using UnityEngine;

namespace PvePanelMod
{
    /// <summary>
    /// 面板尺寸 = 草图里的数字 × k。k = 屏幕高 / 1125（跟着游戏自己的界面缩放走）× 配置的缩放比例 UiScale。
    /// 四块面板和悬停小字都用它，所以一个比例就能整体放大缩小（不同屏幕、不同视距）。
    /// </summary>
    internal static class PanelScale
    {
        public const float Min = 0.8f, Max = 1.5f, Step = 0.1f;
        private static float _value = 1f;

        public static float Value
        {
            get { return _value; }
            set { _value = Mathf.Clamp(Mathf.Round(value * 10f) / 10f, Min, Max); }
        }

        public static float K(GRoot root) => root == null ? 1f : root.height / 1125f * _value;
        public static float K() => K(GRoot.inst);

        /// <summary>放大后可能超出屏幕底边：整块往上挪，保证都在屏幕里（但不高过 minY）。</summary>
        public static float FitBottom(float y, float height, float minY = 0f)
        {
            var root = GRoot.inst;
            if (root == null) return y;
            float maxY = root.height - height - 6f * K(root);
            return Mathf.Max(minY, Mathf.Min(y, maxY));
        }
    }
}
