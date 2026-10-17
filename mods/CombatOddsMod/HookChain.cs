using System;

namespace CombatOddsMod
{
    /// <summary>
    /// RPC 回调的包装策略(纯逻辑, 可离线测试)。
    ///
    /// 两条必须遵守的规则, 违反任何一条都会让一条消息被处理几十次直至栈溢出:
    ///   1. 包装器必须捕获安装时的原始委托, 不能在调用时读可变的静态字段——那个字段会指向更新的包装,
    ///      形成"包装调用自己"的自我递归;
    ///   2. 不能无上限地重复安装。已经在我们自己的包装外层(字段就是我们的包装)时不再安装;
    ///      每局允许的安装次数有上限, 防止别的组件在我们上面反复包装时链越叠越长。
    ///
    /// 只用于 SDK 不包装的回调(如 <c>RoundStartS2C</c>): SDK 会每秒重挂它自己包装的回调,
    /// 再叠一层就会互相把对方装进链里。
    /// </summary>
    internal static class HookChain
    {
        /// <summary>返回应当安装的包装; 不需要安装时返回 null。</summary>
        /// <param name="current">字段当前的值。</param>
        /// <param name="installed">我们上次安装的包装。</param>
        /// <param name="budgetLeft">本局还允许安装几次。</param>
        /// <param name="wrap">用原始委托构造包装。</param>
        internal static TDelegate Build<TDelegate>(TDelegate current, TDelegate installed, int budgetLeft,
            Func<TDelegate, TDelegate> wrap) where TDelegate : Delegate
        {
            if (current == null || wrap == null) return null;
            if (ReferenceEquals(current, installed)) return null; // 已经在最外层
            if (budgetLeft <= 0) return null;                     // 本局安装次数用尽, 不再加层
            return wrap(current);
        }
    }
}
