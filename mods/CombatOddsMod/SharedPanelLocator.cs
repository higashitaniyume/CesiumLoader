using FairyGUI;
using UnityEngine;

namespace CombatOddsMod
{
    /// <summary>
    /// 共享 UIBattleInfoPanel 定位器: 全 mod 只走一处 Walk, 且结果在对象存活期间直接复用。
    /// 之前 HandCount/SkillCooldown/PlayerAttr/HudAtkBonus 各自独立 Walk(深度 12),
    /// 同一帧会重复扫 3~4 次整棵 GRoot。改为这里单次缓存、失效才重扫, 最多 500ms 一次。
    /// </summary>
    internal static class SharedPanelLocator
    {
        private static UI.UIBattleInfoPanel _panel;
        private static float _nextSearchAt;

        public static UI.UIBattleInfoPanel Get()
        {
            if (_panel != null && !_panel.isDisposed && _panel.parent != null) return _panel;
            float now = Time.realtimeSinceStartup;
            if (now < _nextSearchAt) return null;
            _nextSearchAt = now + 0.5f;
            _panel = Walk(GRoot.inst, 0);
            return _panel;
        }

        public static void Invalidate()
        {
            _panel = null;
            _nextSearchAt = 0f;
        }

        private static UI.UIBattleInfoPanel Walk(GObject node, int depth)
        {
            if (node == null || depth > 12) return null;
            if (node is UI.UIBattleInfoPanel panel) return panel;
            if (node is GComponent component)
            {
                int n = component.numChildren;
                for (int i = 0; i < n; i++)
                {
                    var found = Walk(component.GetChildAt(i), depth + 1);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
