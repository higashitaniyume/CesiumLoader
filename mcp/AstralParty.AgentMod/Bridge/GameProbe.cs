using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK;
using GameLogic;
using party.protocol;
using Tools;

namespace AstralParty.AgentMod.Bridge
{
    /// <summary>
    /// 倒计时 / 棋盘 / 用牌窗口的探针。
    ///
    /// 全部走"编译期能拿到的类型 + 少量私有字段反射", 依据是反编译结论(见 docs/MCP-Agent桥接.md):
    ///   - <c>GameLogic.OperationTimer</c>(public static): <c>GetOperateTimer(sn)</c> → Timer.GetTimeRemaining()
    ///     (Timer 是 AOT 类型, 只能反射调用), 兜底读私有静态 <c>operationTime - downtime</c>(秒);
    ///   - <c>ActionLogic.CardSN</c> / <c>UsableCards</c>: 编译期成员, 直接读;
    ///   - 移动候选: <c>BattleLogic.GetSelfPlayerData().CharacterInst.standLand.CanSelectedLandId(fromLandId)</c>,
    ///     编译期成员, 直接读(比反射 MoveArrowManager 的箭头池更稳, 且唯一方向时也有结果)。
    ///
    /// **全部只在主线程调用**(反射 + Unity 对象), 绝不从网络线程进。
    /// </summary>
    internal static class GameProbe
    {
        private static bool _initAttempted;

        private static Type _operationTimerType;
        private static FieldInfo _operationTimeField;
        private static FieldInfo _downtimeField;
        private static FieldInfo _timerDictField;
        private static MethodInfo _getOperateTimerMethod;
        private static MethodInfo _cancelOperateTimerMethod;
        private static MethodInfo _timerGetRemainingMethod;

        private static void EnsureInit()
        {
            if (_initAttempted) return;
            _initAttempted = true;
            try
            {
                // OperationTimer 在热更主程序集里(和 party.protocol 同一个程序集)
                _operationTimerType = typeof(MoveC2S).Assembly.GetType("GameLogic.OperationTimer", false);
                if (_operationTimerType == null) return;

                const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _operationTimeField = _operationTimerType.GetField("operationTime", Static);
                _downtimeField = _operationTimerType.GetField("downtime", Static);
                // 反编译显示字段名是 timerDict(无下划线); MEMORY.md 里写的是 _timerDict —— 两个都试
                _timerDictField = _operationTimerType.GetField("timerDict", Static) ??
                                  _operationTimerType.GetField("_timerDict", Static);
                _getOperateTimerMethod = _operationTimerType.GetMethod("GetOperateTimer", Static);
                _cancelOperateTimerMethod = _operationTimerType.GetMethod("CancelOperatTimer", Static);
            }
            catch { }
        }

        /// <summary>探针是否可用(不可用时 RemainingMs 返回 -1, 不影响其他功能)。</summary>
        public static bool Available { get { EnsureInit(); return _operationTimerType != null; } }

        /// <summary>
        /// 读当前操作倒计时剩余毫秒(-1 = 读不到)。
        /// 优先级: GetOperateTimer(sn).GetTimeRemaining() → operationTime - downtime。
        /// </summary>
        public static long RemainingMs(long sn)
        {
            EnsureInit();
            if (_operationTimerType == null) return -1;

            // 1) 按 sn 取 Timer(AOT 类型 → 反射), 返回类型在元数据里只确认到"float 秒"
            if (sn != 0 && _getOperateTimerMethod != null)
            {
                try
                {
                    object timer = _getOperateTimerMethod.Invoke(null, new object[] { sn });
                    if (timer != null)
                    {
                        if (_timerGetRemainingMethod == null)
                            _timerGetRemainingMethod = timer.GetType().GetMethod("GetTimeRemaining", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (_timerGetRemainingMethod != null)
                        {
                            object v = _timerGetRemainingMethod.Invoke(timer, null);
                            double sec = Convert.ToDouble(v);
                            if (!double.IsNaN(sec) && sec >= 0) return (long)(sec * 1000.0);
                        }
                    }
                }
                catch { }
            }

            // 2) 兜底: 总时长 - 已用时(两个 float 静态字段, 单位秒)
            try
            {
                if (_operationTimeField == null || _downtimeField == null) return -1;
                object total = _operationTimeField.GetValue(null);
                object elapsed = _downtimeField.GetValue(null);
                if (total == null || elapsed == null) return -1;
                double t = Convert.ToDouble(total);
                double d = Convert.ToDouble(elapsed);
                double left = t - d;
                if (double.IsNaN(left)) return -1;
                if (left < 0) left = 0;
                return (long)(left * 1000.0);
            }
            catch { return -1; }
        }

        /// <summary>当前所有正在倒计时的操作 sn(= 服务器等我应答的集合)。读不到返回空数组。</summary>
        public static long[] ActiveOperationSns()
        {
            EnsureInit();
            if (_timerDictField == null) return Array.Empty<long>();
            try
            {
                object dict = _timerDictField.GetValue(null);
                var result = new List<long>();
                var enumerable = dict as IEnumerable;
                if (enumerable == null) return Array.Empty<long>();
                foreach (object key in enumerable)
                {
                    if (key is long l) result.Add(l);
                    else
                    {
                        try { result.Add(Convert.ToInt64(key)); } catch { }
                    }
                }
                return result.ToArray();
            }
            catch { return Array.Empty<long>(); }
        }

        /// <summary>
        /// 取消某个 sn 的倒计时。**必须在动作发送成功之后调用**:
        /// 游戏在每个窗口都挂了"超时自动代打"回调(选第一项/空购买离店),
        /// 我们已经应答了还留着计时器, 到点它会再发一次请求。
        /// </summary>
        public static void CancelOperationTimer(long sn)
        {
            EnsureInit();
            if (sn == 0 || _cancelOperateTimerMethod == null) return;
            try { _cancelOperateTimerMethod.Invoke(null, new object[] { sn }); } catch { }
        }

        // ============================== 用牌窗口 ==============================

        /// <summary>读 ActionLogic 的待响应 sn 与可用牌列表。取不到返回 false。</summary>
        public static bool TryReadCardWindow(out long cardSn, out int[] usableCards, out bool notMove)
        {
            cardSn = 0;
            usableCards = null;
            notMove = false;
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var action = gm != null ? gm.action : null;
                if (action == null) return false;

                cardSn = action.CardSN;
                notMove = action.NotMove;
                var usable = action.UsableCards;
                if (usable != null && usable.Count > 0)
                {
                    var list = new List<int>();
                    var e = ((IEnumerable)usable).GetEnumerator();
                    while (e.MoveNext())
                    {
                        try { list.Add(Convert.ToInt32(e.Current)); } catch { }
                    }
                    usableCards = list.ToArray();
                }
                return true;
            }
            catch { return false; }
        }

        // ============================== 移动候选 ==============================

        /// <summary>
        /// 算"我能走到的地块"。只有轮到我走时才有意义(调用方负责判断窗口)。
        /// 用 Character.standLand.CanSelectedLandId(fromLandId) —— 与客户端建箭头用的是同一个方法。
        /// </summary>
        public static int[] SelfMoveTargets()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var battle = gm != null ? gm.battle : null;
                if (battle == null) return null;

                var selfData = battle.GetSelfPlayerData();
                var ch = selfData != null ? selfData.CharacterInst : null;
                if (ch == null) return null;

                var land = ch.standLand;
                if (land == null) return null;

                var lands = land.CanSelectedLandId(ch.fromLandId);
                if (lands == null) return null;

                var result = new List<int>();
                var e = ((IEnumerable)lands).GetEnumerator();
                while (e.MoveNext())
                {
                    try
                    {
                        int id = Convert.ToInt32(e.Current);
                        if (id != 0) result.Add(id);
                    }
                    catch { }
                }
                return result.ToArray();
            }
            catch { return null; }
        }
    }
}
