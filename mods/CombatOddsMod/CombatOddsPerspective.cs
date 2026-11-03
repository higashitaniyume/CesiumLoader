namespace CombatOddsMod
{
    /// <summary>
    /// 战斗视角判定逻辑：控制何时向玩家展示攻方击杀率与守方防/闪建议。
    /// </summary>
    public static class CombatOddsPerspective
    {
        /// <summary>
        /// 是否显示防守方视角（该防御 / 该闪避与被击倒概率）。
        /// 怪物作为防守方时不具备防/闪二选一决策机制（无法闪避），坚决排除怪物的防守方提示。
        /// </summary>
        public static bool ShouldShowDefender(bool iAmDefender, bool iAmAttacker, bool showForOthers, bool defenderIsMonster)
        {
            if (defenderIsMonster) return false;
            return iAmDefender || (showForOthers && !iAmAttacker);
        }

        /// <summary>
        /// 是否显示攻击方视角（击杀率与预计伤害）。
        /// </summary>
        public static bool ShouldShowAttacker(bool iAmDefender, bool iAmAttacker, bool showForOthers)
        {
            return iAmAttacker || (showForOthers && !iAmDefender);
        }
    }
}
