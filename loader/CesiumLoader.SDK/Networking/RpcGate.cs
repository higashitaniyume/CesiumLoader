using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Core.Net;
using Cysharp.Threading.Tasks;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using Tools;

namespace CesiumLoader.SDK.Networking
{
    /// <summary>
    /// 上行 RPC 的拦截闸门（实验性）：把某个 C2S 请求<b>拦在本地</b>，不发给服务器，
    /// 同时给调用方一个"已本地完成"的返回值，让游戏自己的后续逻辑照常跑。
    ///
    /// <para>
    /// 它<b>不是</b>通用方法 Hook，也不能拦截任意方法。可用范围只有一类：
    /// 走<i>生成式 RPC 类</i>发出的 C2S 请求 —— 也就是
    /// <c>NetManager.inst.RPC.&lt;XxxC2S&gt;.&lt;XxxC2SCall&gt;(req)</c> 这一层。
    /// 游戏里这类生成类共约 200 个，形状完全一致：
    /// <c>public class XxxC2SRPC { public virtual RPCAsyncResult XxxC2SCall(XxxC2S req) =&gt; RPCMsgManager.RPCCallStatic(req, &lt;CMDID&gt;, ProtolcalType.Ahead); }</c>，
    /// 而容器 <c>RPCMsgManager</c> 上每个 RPC 都是一个 <c>public</c> 实例字段。
    /// 因为调用点是对该字段的虚调用，<b>用一个子类覆盖它、再把字段换掉</b>就能拦下这次请求：
    /// 纯托管、一次性、可还原，不需要原生 Hook，也不需要改 IL。
    /// </para>
    ///
    /// <para>
    /// <b>用法（每个 RPC 约 3 行）</b>：mod 侧写一个继承游戏 RPC 类的壳，
    /// 放行时调 <c>base.&lt;XxxC2SCall&gt;(req)</c>（与原版逐字节一致），
    /// 拦截时返回 <see cref="Intercept{TReq}"/> 给的结果：
    /// </para>
    /// <code>
    /// internal sealed class SkinGateRpc : Core.Net.ChooseSkinC2SRPC
    /// {
    ///     internal Func&lt;Core.Net.ChooseSkinC2S, bool&gt; Block;
    ///     public override Core.Net.RPCAsyncResult ChooseSkinC2SCall(Core.Net.ChooseSkinC2S req)
    ///         =&gt; RpcGate.Intercept(req, Block, "SkinGate") ?? base.ChooseSkinC2SCall(req);
    /// }
    ///
    /// // 安装（Dispose 即还原；mod 卸载时必须 Dispose）
    /// _sub = RpcGate.Install&lt;Core.Net.ChooseSkinC2SRPC&gt;(new SkinGateRpc { Block = ShouldBlock }, "SkinGate");
    /// </code>
    ///
    /// <para>
    /// <b>为什么拦截时必须返回一个"稍后完成"的结果</b>：C2S 的调用点普遍写成
    /// <c>RequestXxx(...).OnFinishedOnly.AddOnce(...)</c> —— 先拿到返回值、再把回调挂上去。
    /// 而 <c>Tools.Signal</c> <b>没有</b>"已派发后补挂立刻回调"的重放语义
    /// （<c>AddOnce</c> 只是把委托加进列表，<c>Dispatch</c> 只调用当下在册的监听者）。
    /// 所以：结果必须<b>在调用方挂完回调之后</b>才完成。返回前就把它标成已完成，
    /// 回调永远不会触发（表现是按钮/等待链永久卡住）；返回一个永不完结的结果同样卡住。
    /// <see cref="Intercept{TReq}"/> / <see cref="CompletedLater"/> 的做法是"登记一个动作，
    /// 下一帧在主线程完成"，两个坑都避开。
    /// </para>
    ///
    /// <para>
    /// <b>边界与回退</b>：任何一步不成立（找不到字段、判定抛异常、反射构造结果失败、
    /// 单例尚未就绪）都<b>放行原版请求</b>并记日志 —— 拦截失败退化成"和原版一样"，
    /// 绝不退化成"卡死"。拦截只影响本地：服务器收不到这个请求，因此服务器状态不会改变，
    /// 其它玩家也看不到你在本地做成的样子；被拦下的关键流程 RPC（移动/出牌等）
    /// 只会让自己的客户端与服务端不一致，不会骗过服务器。
    /// </para>
    /// </summary>
    public static class RpcGate
    {
        internal const string Tag = "RPCGATE";

        /// <summary>
        /// 重挂周期（毫秒）。<c>NetManager.Clear()</c> 会执行 <c>RPC = new RPCMsgManager()</c>
        /// 重建整个容器，换过的字段会随之丢失，所以必须周期性检查并重挂；
        /// 1 秒与 SDK 既有的 RPC 回调重挂节奏保持一致。
        /// </summary>
        public const int ReapplyIntervalMs = 1000;

        private static readonly object Gate = new object();
        private static readonly List<Binding> Bindings = new List<Binding>();
        private static readonly HashSet<string> BlockedLogged = new HashSet<string>(StringComparer.Ordinal);
        private static bool _loopStarted;
        private static ConstructorInfo _ctor;
        private static MethodInfo _finish;
        private static bool _resultProbeFailed;

        /// <summary>当前登记的拦截条数（诊断用）。</summary>
        public static int ActiveCount
        {
            get { lock (Gate) return Bindings.Count; }
        }

        private sealed class Binding
        {
            internal Type RpcType;
            internal object Replacement;
            internal string Label;
            internal FieldInfo Field;
            internal object Original;
            internal bool HasOriginal;
            internal object Manager;
        }

        /// <summary>
        /// 安装一个拦截：把 <paramref name="replacement"/> 换进 <c>RPCMsgManager</c> 上
        /// "声明类型恰好是 <typeparamref name="TRpc"/>" 的那个 public 实例字段。
        ///
        /// <para>
        /// <b>泛型实参必须显式写字段的声明类型</b>（即游戏生成类的基类），例如
        /// <c>Install&lt;Core.Net.ChooseSkinC2SRPC&gt;(new SkinGateRpc())</c>。
        /// 不要靠类型推断写成 <c>Install(new SkinGateRpc())</c> —— 那会推断成你的子类类型，
        /// 字段声明类型不匹配因而找不到字段（SDK 会记一条日志并放行，不会静默装错）。
        /// </para>
        ///
        /// <para>
        /// 返回的 <see cref="IDisposable"/>：Dispose 时移除登记，并且<b>只在字段里还是我们的实例时</b>
        /// 写回原实例（不覆盖别人造成的状态）。<b>mod 卸载时必须 Dispose</b>，否则热加载后会留下悬挂实例。
        /// </para>
        /// </summary>
        /// <typeparam name="TRpc">游戏生成 RPC 类的基类（字段声明类型）。</typeparam>
        /// <param name="replacement">要换上去的实例（你的子类）。</param>
        /// <param name="label">日志标签，建议用 mod 名。</param>
        public static IDisposable Install<TRpc>(TRpc replacement, string label = null) where TRpc : class
        {
            string tag = string.IsNullOrEmpty(label) ? Tag : label;
            if (replacement == null)
            {
                SdkLog.Warn(tag, "Install 收到 null 替换实例，本次不拦截");
                return NoopSubscription.Instance;
            }

            Type rpcType = typeof(TRpc);
            if (!rpcType.IsAssignableFrom(replacement.GetType()))
            {
                // 正常不可能发生（编译期已保证），留作防御。
                SdkLog.Warn(tag, "Install 的替换实例类型不匹配 " + rpcType.Name + "，本次不拦截");
                return NoopSubscription.Instance;
            }

            FieldInfo field = null;
            try { field = RpcGateCore.FindField(typeof(RPCMsgManager), rpcType); }
            catch (Exception e) { ModCallbackGuard.ReportOnce(tag + "/find", e); }

            if (field == null)
            {
                SdkLog.Warn(tag, "未在 RPCMsgManager 上找到声明类型为 " + rpcType.Name +
                    " 的唯一 public 字段（游戏更新改了类名/字段名，或 Install 的泛型实参写成了自己的子类）；" +
                    "本次不拦截，行为与原版一致");
                return NoopSubscription.Instance;
            }

            var binding = new Binding
            {
                RpcType = rpcType,
                Replacement = replacement,
                Label = tag,
                Field = field,
            };

            lock (Gate) Bindings.Add(binding);
            SdkLog.Info(tag, "登记上行 RPC 拦截: " + rpcType.Name + " -> " + replacement.GetType().Name +
                "（容器重建后自动重挂；Dispose 还原）");

            ApplyBinding(binding);
            EnsureLoop();
            return new Subscription(binding);
        }

        /// <summary>
        /// 在你的 RPC 壳里调用：返回非 null 表示"拦下这次请求"，直接把它 return 给游戏；
        /// 返回 null 表示"放行"，调用方应 <c>return base.&lt;XxxC2SCall&gt;(req);</c>。
        ///
        /// <para>
        /// <paramref name="shouldBlock"/> 是 mod 代码，跑在游戏调用栈里：为 null、请求为 null
        /// 或抛异常时一律返回 null（放行）并只记一次日志。
        /// 若无法构造"本地完成"的结果也会返回 null（放行）——绝不返回一个永不完成的实例。
        /// </para>
        /// </summary>
        /// <typeparam name="TReq">请求消息类型（如 <c>Core.Net.ChooseSkinC2S</c>）。</typeparam>
        /// <param name="request">本次请求（只读判断；本 API 不修改请求内容）。</param>
        /// <param name="shouldBlock">返回 true 表示不发送这次请求。</param>
        /// <param name="label">日志标签。</param>
        public static RPCAsyncResult Intercept<TReq>(TReq request, Func<TReq, bool> shouldBlock, string label = null)
            where TReq : class
        {
            string tag = string.IsNullOrEmpty(label) ? Tag : label;
            if (!RpcGateCore.ShouldIntercept(request, shouldBlock, tag)) return null;

            RPCAsyncResult result = CompletedLater();
            if (result == null)
            {
                // 拿不到"已完成结果"就绝不能拦：调用方的 .OnFinishedOnly.AddOnce 会永远不触发。
                SdkLog.Warn(tag, "无法构造本地完成结果，本次放行（不拦截）: " + typeof(TReq).Name);
                return null;
            }

            LogBlockedOnce(tag, request);
            return result;
        }

        /// <summary>
        /// 创建一个"下一帧完成"的空 <c>RPCAsyncResult</c>：给需要自己控制拦截时机的 mod 用
        /// （例如先做别的准备工作，再用它返回）。构造失败返回 null（调用方必须放行）。
        /// </summary>
        public static RPCAsyncResult CompletedLater()
        {
            try
            {
                RPCAsyncResult result = CreateResult();
                if (result == null) return null;

                RpcGateCore.Enqueue(() => FinishResult(result));
                KickNextFrameDrain();
                return result;
            }
            catch (Exception e)
            {
                ModCallbackGuard.ReportOnce(Tag + "/completed", e);
                return null;
            }
        }

        private static void LogBlockedOnce(string tag, object request)
        {
            bool first;
            lock (Gate) first = BlockedLogged.Add(tag);
            if (!first) return;

            SdkLog.Info(tag, "已拦截上行 RPC 并在本地完成: " + (request == null ? "?" : request.GetType().Name) +
                "（同一标签只记一次）");
        }

        private static RPCAsyncResult CreateResult()
        {
            if (_resultProbeFailed) return null;
            if (_ctor == null)
            {
                Type type = typeof(RPCAsyncResult);
                ConstructorInfo[] ctors = type.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < ctors.Length; i++)
                {
                    if (ctors[i] != null && ctors[i].GetParameters().Length == 0) { _ctor = ctors[i]; break; }
                }
                if (_ctor == null)
                {
                    _resultProbeFailed = true;
                    SdkLog.Warn(Tag, "RPCAsyncResult 没有可用的无参构造（游戏更新?）；RpcGate 将一直放行");
                    return null;
                }
            }
            return (RPCAsyncResult)_ctor.Invoke(null);
        }

        private static void FinishResult(RPCAsyncResult result)
        {
            if (result == null) return;
            if (_finish == null)
            {
                _finish = typeof(RPCAsyncResult).GetMethod(
                    "Finish", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_finish == null)
                {
                    SdkLog.Warn(Tag, "RPCAsyncResult.Finish 不存在（游戏更新?）；被拦截的调用不会完成，请停止使用 RpcGate");
                    return;
                }
            }
            try { _finish.Invoke(result, null); }
            catch (Exception e) { ModCallbackGuard.ReportOnce(Tag + "/finish", e); }
        }

        private static void KickNextFrameDrain()
        {
            try { DrainNextFrameAsync().Forget(); }
            catch (Exception e) { ModCallbackGuard.ReportOnce(Tag + "/kick", e); }
        }

        private static async UniTaskVoid DrainNextFrameAsync()
        {
            try { await UniTask.Yield(); } catch { }
            try { RpcGateCore.DrainDeferred(); }
            catch (Exception e) { ModCallbackGuard.ReportOnce(Tag + "/drain", e); }
        }

        private static void EnsureLoop()
        {
            lock (Gate)
            {
                if (_loopStarted) return;
                _loopStarted = true;
            }
            try { ReapplyLoopAsync().Forget(); }
            catch
            {
                lock (Gate) _loopStarted = false;
            }
        }

        /// <summary>
        /// 周期性重挂 + 待完成队列兜底。
        ///
        /// 兜底的必要性：正常路径由 <see cref="DrainNextFrameAsync"/> 下一帧完成；
        /// 万一那次 kick 没跑起来（帧循环停摆、异常），这里的 1 秒节拍保证被拦下的调用
        /// 仍会在有限时间内完成，不会把调用方的等待链永久挂住。
        /// </summary>
        private static async UniTaskVoid ReapplyLoopAsync()
        {
            while (true)
            {
                try { await UniTask.Delay(ReapplyIntervalMs); }
                catch { break; }

                bool any;
                lock (Gate) any = Bindings.Count > 0;
                if (!any) break;

                try { RpcGateCore.DrainDeferred(); } catch { }

                Binding[] snapshot;
                lock (Gate) snapshot = Bindings.ToArray();
                for (int i = 0; i < snapshot.Length; i++) ApplyBinding(snapshot[i]);
            }

            lock (Gate) _loopStarted = false;
        }

        private static void ApplyBinding(Binding binding)
        {
            if (binding == null || binding.Field == null) return;
            try
            {
                // 启动早期取 .inst 会 new 出一个游戏自己都不认识的半成品单例（会崩），
                // 必须像 GameEvents 那样先问 hasInstance。
                if (!MonoSingletonProvider<NetManager>.hasInstance) return;
                NetManager net = MonoSingletonProvider<NetManager>.inst;
                RPCMsgManager manager = net == null ? null : net.RPC;
                if (manager == null) return;

                object current = binding.Field.GetValue(manager);
                if (!RpcGateCore.NeedsSwap(current, binding.Replacement)) return;

                if (!ReferenceEquals(binding.Manager, manager))
                {
                    // 新容器（NetManager.Clear 里 RPC = new RPCMsgManager()）：
                    // 重新记录这一次的原实例，还原时才对得上。
                    binding.Manager = manager;
                    binding.Original = current;
                    binding.HasOriginal = true;
                }

                binding.Field.SetValue(manager, binding.Replacement);
            }
            catch (Exception e)
            {
                ModCallbackGuard.ReportOnce(binding.Label + "/apply", e);
            }
        }

        private static void RestoreBinding(Binding binding)
        {
            if (binding == null || binding.Field == null || binding.Manager == null) return;
            try
            {
                object current = binding.Field.GetValue(binding.Manager);
                if (!RpcGateCore.NeedsRestore(current, binding.Replacement)) return;
                if (!binding.HasOriginal) return;

                binding.Field.SetValue(binding.Manager, binding.Original);
                SdkLog.Info(binding.Label, "已还原上行 RPC 拦截: " + binding.RpcType.Name);
            }
            catch (Exception e)
            {
                ModCallbackGuard.ReportOnce(binding.Label + "/restore", e);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private Binding _binding;

            internal Subscription(Binding binding) { _binding = binding; }

            public void Dispose()
            {
                Binding binding = Interlocked.Exchange(ref _binding, null);
                if (binding == null) return;

                lock (Gate) Bindings.Remove(binding);
                RestoreBinding(binding);
            }
        }

        private sealed class NoopSubscription : IDisposable
        {
            internal static readonly NoopSubscription Instance = new NoopSubscription();
            public void Dispose() { }
        }
    }
}
