namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 战斗子阶段。
    ///
    /// 服务器没有下发显式的阶段字段，本值由 <see cref="GameEvents"/> 依据战斗相关 RPC 的
    /// 先后顺序推导（战斗快照 → 攻方投骰）。区分它的原因是 <c>BattleRole.Point</c> 只是
    /// 普通 proto3 整数：攻方尚未投骰和投出 0 点，在消息里都是 0，仅看 <c>Point</c>
    /// 无法判断骰子是否已经落地。
    /// </summary>
    public enum BattlePhase
    {
        /// <summary>当前没有进行中的战斗。</summary>
        None = 0,

        /// <summary>战斗已建立，攻方尚未投骰；此时 <c>BattleRole.Point</c> 不是有效骰点。</summary>
        AttackerRollPending = 1,

        /// <summary>攻方已投骰；<c>BattleRole.Point</c> 是有效点数，可以为 0。</summary>
        AttackerRolled = 2,

        /// <summary>战斗已结束。</summary>
        Ended = 3,
    }
}
