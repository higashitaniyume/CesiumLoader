using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Logging;

namespace CesiumLoader.SDK.Mods
{
    /// <summary>
    /// mod 自己包的 RPC 回调的统一护栏。
    ///
    /// 为什么要它: mod 自己包装 <c>rpc.XXX.OnXXXServerCallBackAsync</c> 时, 回调体是在
    /// <b>游戏自己的 RPC 回调链</b>里执行的(通常还挂在 UniTask 的 await 链上)。
    /// 一旦 mod 的回调体抛异常(最常见的是游戏热更删掉了它引用的协议成员,
    /// 例如 <c>MovePointBuffS2C.Attrs</c>), 异常会直接逃进游戏的链里:
    /// 那个 await 的续体再也不完成 → 掷骰/回合/怪物移动整条流程停摆,
    /// 表现是"画面还能拖, 但游戏不动了"。
    ///
    /// 所以规则是: <b>mod 自己的代码永远不许把异常抛回游戏</b>。异常一律在这里吃掉,
    /// 并且每个回调只记一次日志(否则一次对局能刷几十万行, 把真正的日志淹掉)。
    /// </summary>
    public static class ModCallbackGuard
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>跑一段 mod 自己的回调代码; 抛异常只记一次日志, 不向外抛。</summary>
        /// <param name="label">日志标签(通常用 mod 名)。</param>
        /// <param name="action">要跑的代码。</param>
        public static void Run(string label, Action action)
        {
            if (action == null) return;
            try { action(); }
            catch (Exception e) { ReportOnce(label, e); }
        }

        /// <summary>跑一段有返回值的回调代码; 抛异常时返回 <paramref name="fallback"/>。</summary>
        /// <param name="label">日志标签(通常用 mod 名)。</param>
        /// <param name="action">要跑的代码。</param>
        /// <param name="fallback">出异常时的返回值。</param>
        public static T Run<T>(string label, Func<T> action, T fallback)
        {
            if (action == null) return fallback;
            try { return action(); }
            catch (Exception e) { ReportOnce(label, e); return fallback; }
        }

        /// <summary>记一次异常(同一标签只记一次)。</summary>
        /// <param name="label">日志标签。</param>
        /// <param name="error">异常。</param>
        public static void ReportOnce(string label, Exception error)
        {
            string key = label ?? "?";
            lock (Gate)
            {
                if (!Reported.Add(key)) return;
            }

            string detail = error == null
                ? "unknown"
                : error.GetType().Name + ": " + error.Message;
            SdkLog.Warn(label, "回调异常已忽略(不影响游戏, 但该功能可能无效): " + detail);
        }
    }
}
