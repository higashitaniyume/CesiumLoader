namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 依据战斗相关 RPC 的先后顺序推导战斗子阶段。
    ///
    /// 只处理基础类型（不引用 <c>party.model.Battle</c>），便于离线单测。
    ///
    /// 判定规则：
    /// <list type="bullet">
    /// <item>战斗快照建立后默认为 <see cref="BattlePhase.AttackerRollPending"/>；</item>
    /// <item><c>BattleThrowDiceS2C</c> 报出当前攻方的骰点即视为已投骰，0 点同样有效；</item>
    /// <item>快照里的攻方骰点大于 0 本身就是已投骰的证据（骰点 1..6 只可能在投骰之后出现），
    ///       用于重连后只拿到战斗快照、没有看到投骰广播的情况；</item>
    /// <item>骰点为 0 时快照无法区分“未投骰”和“投出 0 点”，只能依赖投骰广播。</item>
    /// </list>
    /// </summary>
    internal sealed class BattlePhaseTracker
    {
        private long _battleId;
        private long _attackerId;
        private long _defenderId;
        private bool _rolled;
        private int _rolledPoint = -1;

        public BattlePhaseTracker()
        {
            Reset();
        }

        /// <summary>当前战斗子阶段。</summary>
        public BattlePhase Phase { get; private set; }

        /// <summary>当前战斗的攻方是否已经投骰。</summary>
        public bool AttackerHasRolled
        {
            get { return _rolled; }
        }

        /// <summary>攻方骰点：尚未投骰时为 -1，已投骰时为 0..6（0 是有效点数）。</summary>
        public int AttackerPoint
        {
            get { return _rolled ? _rolledPoint : -1; }
        }

        /// <summary>清空到“不在战斗中”。</summary>
        public void Reset()
        {
            _battleId = 0;
            _attackerId = 0;
            _defenderId = 0;
            _rolled = false;
            _rolledPoint = -1;
            Phase = BattlePhase.None;
        }

        /// <summary>
        /// 观察一次战斗快照。
        /// </summary>
        /// <param name="battleId">服务器的战斗 ID。</param>
        /// <param name="attackerId">攻方玩家 ID。</param>
        /// <param name="defenderId">守方玩家 ID。</param>
        /// <param name="isEnd">本场战斗是否已结束。</param>
        /// <param name="attackerPoint">快照里的攻方 <c>BattleRole.Point</c>。</param>
        /// <returns>是否开始了新的一场战斗（此时状态已重置）。</returns>
        public bool ObserveBattle(long battleId, long attackerId, long defenderId, bool isEnd, int attackerPoint)
        {
            bool active = Phase == BattlePhase.AttackerRollPending || Phase == BattlePhase.AttackerRolled;
            bool newBattle = !active
                             || battleId != _battleId
                             || attackerId != _attackerId
                             || defenderId != _defenderId;

            if (newBattle)
            {
                _battleId = battleId;
                _attackerId = attackerId;
                _defenderId = defenderId;
                _rolled = false;
                _rolledPoint = -1;
            }

            if (isEnd)
            {
                Phase = BattlePhase.Ended;
                return newBattle;
            }

            if (!_rolled && attackerPoint > 0)
            {
                _rolled = true;
                _rolledPoint = attackerPoint;
            }

            Phase = _rolled ? BattlePhase.AttackerRolled : BattlePhase.AttackerRollPending;
            return newBattle;
        }

        /// <summary>
        /// 观察攻方投骰广播。
        /// </summary>
        /// <param name="playerId">投骰的玩家 ID。</param>
        /// <param name="point">广播的骰点（0..6）。</param>
        /// <returns>该广播是否属于当前战斗的攻方，且使阶段或骰点发生了变化。</returns>
        public bool ObserveAttackerDice(long playerId, int point)
        {
            if (Phase != BattlePhase.AttackerRollPending && Phase != BattlePhase.AttackerRolled) return false;
            if (playerId != _attackerId) return false;
            if (point < 0) return false;

            bool changed = !_rolled || _rolledPoint != point;
            _rolled = true;
            _rolledPoint = point;
            Phase = BattlePhase.AttackerRolled;
            return changed;
        }
    }
}
