using System;

namespace CombatOddsMod
{
    internal static class SkillCooldownText
    {
        // 剩余值由服务器同步；即使特殊效果使剩余值超过常规上限，也应原样展示。
        internal static string Format(int remaining, int total)
        {
            return "CD " + Math.Max(0, remaining) + "/" + Math.Max(0, total);
        }
    }
}
