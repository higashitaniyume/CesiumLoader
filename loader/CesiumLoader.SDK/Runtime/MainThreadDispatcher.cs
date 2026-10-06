using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using CesiumLoader.SDK.Logging;

namespace CesiumLoader.SDK.Runtime
{
    /// <summary>
    /// 纯托管的主线程调度核心: 只依赖 BCL, 不引用 Unity —— 可以脱离游戏做单元测试。
    /// 队列语义: Post 入队, Drain 在"主线程"上取出执行, 单个回调异常不影响其它回调。
    /// </summary>
    public sealed class MainThreadDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private Func<bool> _isMainThread;
        private long _posted;
        private long _executed;
        private long _failed;

        public MainThreadDispatcher(Func<bool> isMainThread = null)
        {
            _isMainThread = isMainThread ?? (() => false);
        }

        /// <summary>"当前是否主线程"判定函数。测试时可替换为任意谓词。</summary>
        public Func<bool> IsMainThreadPredicate
        {
            get { return _isMainThread; }
            set { _isMainThread = value ?? (() => false); }
        }

        /// <summary>当前排队未执行的回调数。</summary>
        public int PendingCount { get { return _queue.Count; } }

        /// <summary>累计入队数。</summary>
        public long PostedCount { get { return Interlocked.Read(ref _posted); } }

        /// <summary>累计成功执行数。</summary>
        public long ExecutedCount { get { return Interlocked.Read(ref _executed); } }

        /// <summary>累计抛异常数。</summary>
        public long FailedCount { get { return Interlocked.Read(ref _failed); } }

        /// <summary>回调异常出口(默认无操作; SDK 会接到 SdkLog)。</summary>
        public Action<Exception> OnError { get; set; }

        /// <summary>入队(不等待执行)。</summary>
        public void Post(Action action)
        {
            if (action == null) return;
            Interlocked.Increment(ref _posted);
            _queue.Enqueue(action);
        }

        /// <summary>
        /// 入队并等待执行完成。
        /// 已在主线程时直接内联执行(避免自等待死锁); 否则等待 timeoutMs。
        /// </summary>
        public bool PostAndWait(Action action, int timeoutMs = 5000)
        {
            if (action == null) return false;

            if (SafeIsMainThread())
            {
                try { action(); Interlocked.Increment(ref _executed); return true; }
                catch (Exception e) { Interlocked.Increment(ref _failed); RaiseError(e); return false; }
            }

            if (timeoutMs <= 0) { Post(action); return false; }

            // 用轮询等结果, 不要用 ManualResetEventSlim:
            // IL2CPP/HybridCLR 裁剪掉了 ManualResetEventSlim.Wait(int), 游戏内调用抛
            //     MissingMethodException: MethodNotFind System.Threading.ManualResetEventSlim::Wait
            // Thread.Sleep / Environment.TickCount 属于一定存在的基础 BCL 子集。
            int completed = 0;
            Exception captured = null;
            Post(() =>
            {
                try { action(); }
                catch (Exception e) { captured = e; }
                finally { Volatile.Write(ref completed, 1); }
            });

            int startTicks = Environment.TickCount;
            while (Volatile.Read(ref completed) == 0)
            {
                if (unchecked(Environment.TickCount - startTicks) >= timeoutMs) return false;
                try { Thread.Sleep(1); } catch { }
            }

            if (captured != null) { RaiseError(captured); return false; }
            return true;
        }

        /// <summary>取出并执行当前队列中的全部回调, 返回执行个数。</summary>
        public int Drain()
        {
            int count = 0;
            Action action;
            while (_queue.TryDequeue(out action))
            {
                count++;
                try { action(); Interlocked.Increment(ref _executed); }
                catch (Exception e) { Interlocked.Increment(ref _failed); RaiseError(e); }
            }
            return count;
        }

        /// <summary>丢弃所有排队回调(不执行)。</summary>
        public void Clear()
        {
            Action dropped;
            while (_queue.TryDequeue(out dropped)) { }
        }

        private bool SafeIsMainThread()
        {
            try { return _isMainThread(); }
            catch { return false; }
        }

        private void RaiseError(Exception e)
        {
            try { OnError?.Invoke(e); } catch { }
        }
    }
}
