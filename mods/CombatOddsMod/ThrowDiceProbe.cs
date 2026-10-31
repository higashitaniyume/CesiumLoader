using System;
using System.Collections.Generic;
using Core.Net;
using Cysharp.Threading.Tasks;
using GameLogic;
using party.protocol;
using Tools;

namespace CombatOddsMod
{
    /// <summary>
    /// 记录「<c>ThrowDiceS2C</c> 里服务器实际给的移动点数」—— 这是客户端能拿到的、包含全部来源
    /// (筹码常驻 + buff 临时 + 地图事件)的移速加成的**唯一权威值**:
    /// 加成 = <c>MovePoint - ΣVals</c>。
    ///
    /// 为什么必须自己挂这个 RPC:
    ///   · <c>BattleProperty.ExtraAddMovePoint</c> 只在服务器主动推送 AddMove 变化时才有值,
    ///     常驻筹码加成在客户端可能一直是 0(实测持有速度轮滑-中级, 连续投骰都多走 2 格而属性是 0)。
    ///   · SDK 的 <c>GameEvents</c> 没有包装 <c>ThrowDiceS2C</c>(只包了 ThrowDiceResult/BattleThrowDice/Move),
    ///     所以这里按"只包 SDK 不碰的 RPC"的约定自己包一层, 并在创建时**捕获原始委托**(不能读可变静态字段)。
    ///
    /// 每场战斗游戏会重置回调, 因此 <see cref="EnsureHooked"/> 需要每秒调用一次(幂等)。
    /// 回放同样走 RPC 注入, 所以回放里也能记录到同一份差值。
    /// </summary>
    internal static class ThrowDiceProbe
    {
        internal struct RollInfo
        {
            public int DiceSum;
            public int MovePoint;
            public int Delta;
            public int KnownAtRoll;
            /// <summary>投骰这一刻客户端的"加速−减速"值, 用来判断之后服务器是否又推了新值。</summary>
            public int PropNetAtRoll;
            /// <summary>投骰这一刻客户端模型的预测值(用于学习"看不见的常驻差额")。</summary>
            public int BasePredictedAtRoll;
        }

        /// <summary>由 ModEntry 注入: 给定玩家 id, 用客户端可见数据算出一个预测值。</summary>
        internal static Func<long, int> BasePredictor;

        private static readonly Dictionary<long, RollInfo> _last = new Dictionary<long, RollInfo>();
        private static ThrowDiceS2CRPC.OnThrowDiceS2CServerDelegate _wrapped;
        private static ThrowDiceS2CRPC.OnThrowDiceS2CServerDelegate _original;

        /// <summary>把记录器挂到 <c>ThrowDiceS2C</c> 上(幂等)。</summary>
        internal static void EnsureHooked()
        {
            try
            {
                var net = MonoSingletonProvider<NetManager>.inst;
                var rpc = net != null ? net.RPC : null;
                if (rpc == null || rpc.ThrowDiceS2C == null) return;

                var current = rpc.ThrowDiceS2C.OnThrowDiceS2CServerCallBackAsync;
                if (current == null || ReferenceEquals(current, _wrapped)) return;

                // 创建时捕获原始委托(闭包), 不在回调里读静态字段 —— 避免把自己包进自己。
                var original = current;
                ThrowDiceS2CRPC.OnThrowDiceS2CServerDelegate wrapped = (model, errId, isDispatch) =>
                {
                    Record(model);
                    if (original != null) return original(model, errId, isDispatch);
                    return UniTask.CompletedTask;
                };

                rpc.ThrowDiceS2C.OnThrowDiceS2CServerCallBackAsync = wrapped;
                _wrapped = wrapped;
                _original = original;
            }
            catch { }
        }

        /// <summary>取消挂钩(仅在当前回调仍是我们包的那层时恢复原委托)。</summary>
        internal static void Unhook()
        {
            try
            {
                var net = MonoSingletonProvider<NetManager>.inst;
                var rpc = net != null ? net.RPC : null;
                if (rpc != null && rpc.ThrowDiceS2C != null &&
                    _wrapped != null && ReferenceEquals(rpc.ThrowDiceS2C.OnThrowDiceS2CServerCallBackAsync, _wrapped))
                {
                    rpc.ThrowDiceS2C.OnThrowDiceS2CServerCallBackAsync = _original;
                }
            }
            catch { }
            _wrapped = null;
            _original = null;
        }

        internal static bool TryGet(long playerId, out RollInfo info)
        {
            return _last.TryGetValue(playerId, out info);
        }

        internal static void Reset()
        {
            _last.Clear();
        }

        private static void Record(ThrowDiceS2C model)
        {
            try
            {
                if (model == null) return;

                int sum = 0;
                var vals = model.Vals;
                if (vals != null)
                {
                    for (int i = 0; i < vals.Count; i++) sum += vals[i];
                }

                int known = 0;
                int propNet = 0;
                try
                {
                    var pd = SimpleSingletonProvider<GameLogicManager>.inst?.battle?.GetPlayerDataById(model.PlayerId);
                    if (pd != null)
                    {
                        known = RelicMoveBonus.Total(pd.GetRelicIds());
                        if (pd.Property != null)
                        {
                            propNet = pd.Property.ExtraAddMovePoint.Value - pd.Property.ExtraMinusMovePoint.Value;
                        }
                    }
                }
                catch { }

                int basePred = 0;
                try
                {
                    var predictor = BasePredictor;
                    if (predictor != null) basePred = predictor(model.PlayerId);
                }
                catch { }

                _last[model.PlayerId] = new RollInfo
                {
                    DiceSum = sum,
                    MovePoint = model.MovePoint,
                    Delta = model.MovePoint - sum,
                    KnownAtRoll = known,
                    PropNetAtRoll = propNet,
                    BasePredictedAtRoll = basePred,
                };

                // 学习"客户端看不见但稳定存在"的差额(建筑、隐藏被动等)。
                MoveResidual.Learn(model.PlayerId, model.MovePoint - sum, basePred);
            }
            catch { }
        }
    }
}
