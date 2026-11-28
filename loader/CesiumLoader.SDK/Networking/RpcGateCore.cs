using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Mods;

namespace CesiumLoader.SDK.Networking
{
    /// <summary>
    /// <see cref="RpcGate"/> 的纯逻辑内核：<b>不引用任何游戏类型</b>。
    ///
    /// 这样切分的目的只有测试：SDK 的桌面测试进程不加载 <c>AstralParty.Runtime.dll</c>
    /// (见 CesiumLoader.SDK.Tests 的引用配置)，任何引用游戏类型的成员在测试里一 JIT 就抛
    /// "could not load file or assembly"。把字段发现、判定护栏、延迟完成队列这些真正需要
    /// 覆盖的逻辑放进这里，游戏类型(RPCMsgManager/NetManager/RPCAsyncResult)只留在 RpcGate 那一层。
    /// </summary>
    internal static class RpcGateCore
    {
        private static readonly object Gate = new object();
        private static readonly Queue<Action> Deferred = new Queue<Action>();

        /// <summary>
        /// 在 <paramref name="managerType"/> 上找"声明类型恰好是 <paramref name="rpcType"/>"的
        /// public 实例字段。找不到返回 null；同类型有<b>多个</b>字段也返回 null(fail-safe：
        /// 拦错字段比不拦危险得多，调用方必须报错并放行)。
        /// </summary>
        internal static FieldInfo FindField(Type managerType, Type rpcType)
        {
            if (managerType == null || rpcType == null) return null;

            FieldInfo found = null;
            FieldInfo[] fields = managerType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (f == null || f.FieldType != rpcType) continue;
                if (found != null) return null;   // 歧义 → 视为找不到
                found = f;
            }
            return found;
        }

        /// <summary>
        /// 判定这次上行调用是否要拦下。
        ///
        /// 判定体是 mod 代码，跑在<b>游戏自己的调用栈</b>里(和 ModCallbackGuard 描述的 RPC 回调同理)：
        /// 它抛出的异常绝不能外泄，判不出来就必须让原版请求照发。因此
        /// predicate 为 null、request 为 null、或 predicate 抛异常，一律返回 false(放行)。
        /// </summary>
        internal static bool ShouldIntercept<TReq>(TReq request, Func<TReq, bool> predicate, string label)
            where TReq : class
        {
            if (predicate == null || request == null) return false;
            try { return predicate(request); }
            catch (Exception e)
            {
                ModCallbackGuard.ReportOnce(label ?? "RPCGATE", e);
                return false;
            }
        }

        /// <summary>把一个"稍后完成"的动作排进队列。</summary>
        internal static void Enqueue(Action complete)
        {
            if (complete == null) return;
            lock (Gate) Deferred.Enqueue(complete);
        }

        /// <summary>队列中待完成的条数。</summary>
        internal static int PendingDeferred
        {
            get { lock (Gate) return Deferred.Count; }
        }

        /// <summary>
        /// 执行全部待完成动作，返回执行条数。单个动作抛异常只记日志，不影响其它动作
        /// (阻塞调用方的续体必须尽量完成，否则按钮/等待链会卡住)。
        /// </summary>
        internal static int DrainDeferred()
        {
            int done = 0;
            while (true)
            {
                Action next;
                lock (Gate)
                {
                    if (Deferred.Count == 0) break;
                    next = Deferred.Dequeue();
                }
                try { next(); }
                catch (Exception e) { ModCallbackGuard.ReportOnce("RPCGATE/drain", e); }
                done++;
            }
            return done;
        }

        /// <summary>当前字段值是否需要换成我们的实例。</summary>
        internal static bool NeedsSwap(object current, object replacement)
        {
            return !ReferenceEquals(current, replacement);
        }

        /// <summary>
        /// 卸载时是否可以还原：只有当字段里还放着<b>我们的</b>实例时才写回原实例。
        /// 别人(或游戏自己)已经改过就什么都不做 —— 不覆盖非我们造成的状态。
        /// </summary>
        internal static bool NeedsRestore(object current, object replacement)
        {
            return ReferenceEquals(current, replacement);
        }

        /// <summary>测试隔离：清空待完成队列。</summary>
        internal static void ResetForTest()
        {
            lock (Gate) Deferred.Clear();
        }
    }
}
