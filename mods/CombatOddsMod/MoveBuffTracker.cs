using System.Collections.Generic;
using CesiumLoader.SDK.Gameplay;
using Core.Net;
using Cysharp.Threading.Tasks;
using GameLogic;
using party.protocol;
using Tools;

namespace CombatOddsMod
{
    /// <summary>
    /// 学习「哪些 Buff 会改变移速、各改变多少」, 用来在投骰前预测移速加成。
    ///
    /// 数据来源: 服务器每次移动后都会下发 <c>MovePointBuffS2C</c>(每个 Buff 一条
    /// <c>AdditionAttribute</c>, <c>UniqueId</c> 是该 Buff 实例, <c>MovePoint</c> 是它对移动的贡献)。
    /// 游戏自己只用它弹一个「+N 移动」的提示就丢掉了, 这里把贡献值按 **Buff 配置 id** 记下来,
    /// 之后只要该 Buff 还挂在玩家身上, 就能在投骰前算出这部分加成。
    ///
    /// 局限(客户端拿不到, 只能等投骰差值):
    ///   · 从没触发过移动的 Buff(还没机会学到它的数值);
    ///   · 角色被动、地图事件等根本不作为 Buff 下发的常驻加成。
    ///
    /// SDK 没有包装 <c>MovePointBuffS2C</c>, 这里按约定自行包一层, 创建时捕获原始委托。
    /// </summary>
    internal static class MoveBuffTracker
    {
        /// <summary>Buff 配置 id → 该 Buff 每次移动贡献的移速点数(可能为负)。</summary>
        private static readonly Dictionary<int, int> _byBuffConfig = new Dictionary<int, int>();

        private static MovePointBuffS2CRPC.OnMovePointBuffS2CServerDelegate _wrapped;
        private static MovePointBuffS2CRPC.OnMovePointBuffS2CServerDelegate _original;

        internal static void EnsureHooked()
        {
            try
            {
                var net = MonoSingletonProvider<NetManager>.inst;
                var rpc = net != null ? net.RPC : null;
                if (rpc == null || rpc.MovePointBuffS2C == null) return;

                var current = rpc.MovePointBuffS2C.OnMovePointBuffS2CServerCallBackAsync;
                if (current == null || ReferenceEquals(current, _wrapped)) return;

                var original = current;
                MovePointBuffS2CRPC.OnMovePointBuffS2CServerDelegate wrapped = (model, errId, isDispatch) =>
                {
                    Record(model);
                    if (original != null) return original(model, errId, isDispatch);
                    return UniTask.CompletedTask;
                };

                rpc.MovePointBuffS2C.OnMovePointBuffS2CServerCallBackAsync = wrapped;
                _wrapped = wrapped;
                _original = original;
            }
            catch { }
        }

        internal static void Unhook()
        {
            try
            {
                var net = MonoSingletonProvider<NetManager>.inst;
                var rpc = net != null ? net.RPC : null;
                if (rpc != null && rpc.MovePointBuffS2C != null &&
                    _wrapped != null && ReferenceEquals(rpc.MovePointBuffS2C.OnMovePointBuffS2CServerCallBackAsync, _wrapped))
                {
                    rpc.MovePointBuffS2C.OnMovePointBuffS2CServerCallBackAsync = _original;
                }
            }
            catch { }
            _wrapped = null;
            _original = null;
        }

        internal static void Reset()
        {
            _byBuffConfig.Clear();
        }

        /// <summary>
        /// 当前挂在玩家身上、且已知数值的移速 Buff 合计(含表格与服务器教过的数值, 含负数)。
        /// 返回 0 表示"没有已知的 Buff 移速"。
        /// </summary>
        internal static int Predict(long playerId)
        {
            try
            {
                var buffs = Players.BuffsOf(playerId);
                if (buffs == null || buffs.Count == 0) return 0;

                var list = new List<ActiveBuff>(buffs.Count);
                for (int i = 0; i < buffs.Count; i++)
                {
                    list.Add(new ActiveBuff { BuffId = buffs[i].BuffId, Layers = buffs[i].Layers });
                }
                return MoveBuffTable.Sum(list, _byBuffConfig);
            }
            catch { return 0; }
        }

        private static void Record(MovePointBuffS2C model)
        {
            try
            {
                if (model == null || model.Attrs == null || model.Attrs.Count == 0) return;

                BattlePlayerData pd = null;
                try
                {
                    pd = SimpleSingletonProvider<GameLogicManager>.inst?.battle?.GetPlayerDataById(model.PlayerId);
                }
                catch { }

                for (int i = 0; i < model.Attrs.Count; i++)
                {
                    var attr = model.Attrs[i];
                    if (attr == null) continue;

                    int configId = 0;
                    try
                    {
                        var buff = pd != null && pd.buffContainer != null
                            ? pd.buffContainer.GetBuffById(attr.UniqueId)
                            : null;
                        if (buff != null) configId = buff.BuffId;
                    }
                    catch { }

                    // 拿不到配置 id(该 Buff 已经消耗掉)就跳过: 一次性效果本来也不该留到下一投。
                    if (configId == 0) continue;
                    // 数值随层数/上下文变化的特殊 Buff 不走"记住数值"这条路, 由 MoveBuffTable 现算。
                    if (MoveBuffTable.IsSpecial(configId)) continue;
                    _byBuffConfig[configId] = attr.MovePoint;
                }
            }
            catch { }
        }
    }
}
