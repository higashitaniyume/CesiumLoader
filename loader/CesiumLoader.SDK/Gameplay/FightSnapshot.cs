namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 一场战斗结算完成的快照(<see cref="GameEvents.FightResolved"/>)。
    /// 只给事实(谁打谁、谁追击、追击伤害), 归属判断与 HP 对账由调用方做。
    /// </summary>
    public struct FightSnapshot
    {
        /// <summary>战斗 id。</summary>
        public long BattleId;

        /// <summary>攻击方玩家。</summary>
        public long AttackerPlayerId;

        /// <summary>防守方玩家。</summary>
        public long DefenderPlayerId;

        /// <summary>追击方玩家(0 = 无追击, 调用方不要拆分)。</summary>
        public long ChainAttackerPlayerId;

        /// <summary>防守方增益 HP(原始字段)。</summary>
        public int IncHp;

        /// <summary>追击伤害。</summary>
        public int ChainAttackDamage;

        /// <summary>是否已结束(本事件只在结束时推送, 恒真)。</summary>
        public bool Ended;

        /// <summary>是否重连战斗(重连信息不完整时不推送, 恒假)。</summary>
        public bool Reconnected;
    }
}
