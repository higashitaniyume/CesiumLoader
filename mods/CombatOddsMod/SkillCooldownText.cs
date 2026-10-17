using System;

namespace CombatOddsMod
{
    internal static class SkillCooldownText
    {
        // 剩余值由服务器同步；即使特殊效果使剩余值超过常规上限，也应原样展示。
        internal static string Format(int remaining, int total)
        {
            int left = Math.Max(0, remaining);
            int cap = Math.Max(total, left);   // 上限还没学到时也不能出现 "3/2"
            return "CD " + left + "/" + Math.Max(0, cap);
        }
    }
}
