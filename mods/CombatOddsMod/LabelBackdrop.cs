using FairyGUI;
using UnityEngine;

namespace CombatOddsMod
{
    /// <summary>
    /// 画在棋盘上的小标签（头顶攻防、怪物技能 CD）后面垫一块深色圆角底，压在花哨的地图上也看得清。
    /// 开关是配置项 LabelBackground（默认开）；关掉就和原来一样只有描边字。
    /// 颜色取 PVE 面板的深色底（启动器 v2 配色），和面板统一。
    /// </summary>
    internal static class LabelBackdrop
    {
        public static bool Enabled = true;
        private static readonly Color Fill = new Color(0.051f, 0.059f, 0.094f, 0.88f);

        /// <summary>
        /// 有底块时去掉文字的黑描边（小字号下描边会盖住白色笔画，看起来整个字是黑的），关掉底块时恢复原来的描边。
        /// 只改这一个 mod 自己建的文字，不碰游戏的元素。
        /// </summary>
        public static void ApplyOutline(GTextField text, float outlineWhenNoBackdrop)
        {
            if (text == null || text.isDisposed) return;
            var tf = text.textFormat;
            if (tf == null) return;
            float want = Enabled ? 0f : outlineWhenNoBackdrop;
            if (System.Math.Abs(tf.outline - want) < 0.01f && tf.color == Color.white) return;
            tf.outline = want;
            tf.color = Color.white;
            text.textFormat = tf;
        }

        /// <summary>按文字的位置和大小摆好底块；关掉开关或文字不可见时隐藏底块。</summary>
        public static void Fit(GGraph bg, GTextField text, float padX, float padY)
        {
            if (bg == null || bg.isDisposed) return;
            if (!Enabled || text == null || text.isDisposed || !text.visible) { bg.visible = false; return; }
            float w = text.textWidth + padX * 2f, h = text.textHeight + padY * 2f, r = Mathf.Min(9f, h / 2f);
            bg.SetXY(text.x - padX, text.y - padY);
            bg.DrawRoundRect(w, h, Fill, new[] { r, r, r, r });
            bg.visible = true;
        }
    }
}
