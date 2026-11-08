namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 一名玩家的一局最终统计(<see cref="GameEvents.MatchSettled"/>)。
    /// 值是服务器最终值, 与本地事件累计口径不同, 只用于已结算状态与对账,
    /// 不要覆盖本地累计。
    /// </summary>
    public sealed class PlayerFinalStats
    {
        /// <summary>玩家。</summary>
        public long PlayerId;

        /// <summary>击杀数。</summary>
        public long KillCount;

        /// <summary>总伤害。</summary>
        public long TotalDamage;

        /// <summary>总承伤。</summary>
        public long TotalInjured;

        /// <summary>总死亡。</summary>
        public long TotalDie;

        /// <summary>治疗分。</summary>
        public long TreatmentScore;

        /// <summary>移动点。</summary>
        public long MovePoint;

        /// <summary>总星币。</summary>
        public long TotalGold;

        /// <summary>PVE 转账星币。</summary>
        public long PveTransferGold;

        public override string ToString()
        {
            return "结算(pid=" + PlayerId + ", 击杀=" + KillCount +
                   ", 伤害=" + TotalDamage + ", 承伤=" + TotalInjured +
                   ", 星币=" + TotalGold + ")";
        }
    }
}
