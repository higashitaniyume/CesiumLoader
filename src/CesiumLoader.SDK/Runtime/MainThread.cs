using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace CesiumLoader.SDK
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

    /// <summary>
    /// 主线程调度器(SDK 基础设施)。
    ///
    /// 为什么需要它: mod 入口由原生加载器的 <b>boot 线程</b>通过 il2cpp_runtime_invoke
    /// 调用, 不是 Unity 主线程; 而 UnityEngine.Object / Camera / Transform 等 API
    /// 只能在主线程访问。所有跨线程访问 Unity 的调用都应经由此处。
    ///
    /// 实现: 用 UniTask 的 PlayerLoop 注册每帧回调(UniTask.Post)作为泵, 在 Update /
    /// PostLateUpdate 时机排空队列。UniTask 的 PlayerLoop 在本游戏已确认可用
    /// (SDK 既有 ModBase.Run 就是靠 UniTask.Delay 从 boot 线程切回主线程的)。
    ///
    /// 分层: 纯队列逻辑在 <see cref="MainThreadDispatcher"/>, 可离线测试;
    /// 本类只负责"谁来 Drain"。
    /// </summary>
    public static class MainThread
    {
        private static readonly MainThreadDispatcher _dispatcher = new MainThreadDispatcher(() => IsMainThread);

        private static readonly Action _frameCallback = OnFrame;
        private static readonly Action _lateFrameCallback = OnLateFrame;

        private static int _mainThreadId;
        private static int _frameCount;
        private static int _running;
        private static int _started;
        private static int _warnedNoTick;
        private static int _warnedNotMainThread;
        private static Timer _watchdog;

        /// <summary>默认等待超时(毫秒)。</summary>
        public static int SendTimeoutMs = 5000;

        // ---------- 状态 ----------

        /// <summary>主线程泵是否在运行。</summary>
        public static bool IsPumpRunning { get { return Volatile.Read(ref _running) != 0; } }

        /// <summary>是否已经确认主线程 id。</summary>
        public static bool HasMainThread { get { return Volatile.Read(ref _mainThreadId) != 0; } }

        /// <summary>主线程托管 id(未确认时为 0)。</summary>
        public static int MainThreadId { get { return Volatile.Read(ref _mainThreadId); } }

        /// <summary>当前线程是否 Unity 主线程。主线程尚未确认时保守返回 false。</summary>
        public static bool IsMainThread
        {
            get
            {
                int id = Volatile.Read(ref _mainThreadId);
                return id != 0 && Thread.CurrentThread.ManagedThreadId == id;
            }
        }

        /// <summary>已泵出的帧数(Update 时机)。</summary>
        public static int FrameCount { get { return Volatile.Read(ref _frameCount); } }

        /// <summary>排队未执行的回调数。</summary>
        public static int PendingCount { get { return _dispatcher.PendingCount; } }

        /// <summary>累计入队数。</summary>
        public static long PostedCount { get { return _dispatcher.PostedCount; } }

        /// <summary>累计执行数。</summary>
        public static long ExecutedCount { get { return _dispatcher.ExecutedCount; } }

        /// <summary>累计失败数。</summary>
        public static long FailedCount { get { return _dispatcher.FailedCount; } }

        // ---------- 生命周期 ----------

        /// <summary>启动主线程泵(幂等)。SDK 首次需要主线程时会自动调用。</summary>
        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;

            _dispatcher.OnError = e => SdkLog.Error("MAINTHREAD", "主线程回调异常: " + e);

            // 先启动泵 —— 这是功能主干, 必须不能被后面的可选步骤拖累。
            // _started 是一次性标志, 一旦这里抛出去就再也不会重试, 整个进程的主线程
            // 调度都会永久失效(2026-09-20 游戏内实测过这个后果)。
            try
            {
                Volatile.Write(ref _running, 1);
                ScheduleFrame();
                SdkLog.Info("MAINTHREAD", "主线程泵已启动 (UniTask PlayerLoop)");
            }
            catch (Exception e)
            {
                Volatile.Write(ref _running, 0);
                SdkLog.Warn("MAINTHREAD", "主线程泵启动失败, 将退化为按需 Post: " + e.Message);
            }

            // 可选: 在第一帧之前就拿到主线程 id, 让 IsMainThread 提前可用。
            // 放在泵之后: 即使它失败(或抛出的异常穿透了内部 catch), 泵也已经起来了。
            TryCaptureMainThreadIdFromUniTask();

            StartWatchdog();
        }

        /// <summary>停止主线程泵(队列保留)。用于测试/放弃。</summary>
        public static void Shutdown()
        {
            Volatile.Write(ref _running, 0);
        }

        /// <summary>确保泵已启动。</summary>
        private static void EnsureStarted()
        {
            if (Volatile.Read(ref _started) == 0) Initialize();
        }

        // ---------- 调度 API ----------

        /// <summary>投递到主线程(不等待)。可从任意线程调用。</summary>
        public static void Post(Action action)
        {
            if (action == null) return;
            EnsureStarted();
            _dispatcher.Post(action);
        }

        /// <summary>
        /// 投递并等待执行。<b>不要在主线程上等待由主线程完成的工作</b>(会超时而不是死锁)。
        /// </summary>
        public static bool Send(Action action, int timeoutMs = -1)
        {
            if (action == null) return false;
            EnsureStarted();
            return _dispatcher.PostAndWait(action, timeoutMs < 0 ? SendTimeoutMs : timeoutMs);
        }

        /// <summary>
        /// 在(或切到)主线程执行。已在主线程时内联执行, 否则投递并等待。
        /// 超时不会抛异常, 只记录错误 —— SDK 不应拖垮游戏。
        /// </summary>
        public static void Run(Action action)
        {
            if (action == null) return;
            if (IsMainThread) { _dispatcher.PostAndWait(action, 0); return; }
            if (!Send(action)) SdkLog.Error("MAINTHREAD", "Run 超时: 主线程未在 " + SendTimeoutMs + "ms 内执行回调");
        }

        /// <summary>在主线程执行并返回结果。超时/异常时返回 default(T)。</summary>
        public static T Run<T>(Func<T> func)
        {
            if (func == null) return default(T);
            if (IsMainThread) { try { return func(); } catch (Exception e) { SdkLog.Error("MAINTHREAD", "Run<T> 异常: " + e); return default(T); } }

            T result = default(T);
            bool ok = Send(() => { result = func(); });
            if (!ok) SdkLog.Error("MAINTHREAD", "Run<T> 超时: 主线程未在 " + SendTimeoutMs + "ms 内执行回调");
            return result;
        }

        /// <summary>在主线程执行并返回结果, 失败时返回 fallback。</summary>
        public static T Run<T>(Func<T> func, T fallback)
        {
            if (func == null) return fallback;
            if (IsMainThread) { try { return func(); } catch { return fallback; } }
            T result = fallback;
            bool ok = Send(() => { result = func(); });
            return ok ? result : fallback;
        }

        /// <summary>异步投递到主线程; 返回的 Task 在该回调执行后完成。</summary>
        public static Task RunAsync(Action action)
        {
            var tcs = new TaskCompletionSource<object>();
            if (action == null) { tcs.TrySetResult(null); return tcs.Task; }

            EnsureStarted();
            _dispatcher.Post(() =>
            {
                try { action(); tcs.TrySetResult(null); }
                catch (Exception e) { tcs.TrySetException(e); }
            });
            return tcs.Task;
        }

        /// <summary>
        /// 断言当前在主线程。仅记录警告(不抛异常, 不拖垮游戏), 返回是否满足。
        /// </summary>
        public static bool AssertMainThread(string context = null)
        {
            if (IsMainThread) return true;

            if (Interlocked.Exchange(ref _warnedNotMainThread, 1) == 0)
                SdkLog.Warn("MAINTHREAD", "检测到非主线程调用 Unity API" +
                    (string.IsNullOrEmpty(context) ? "" : " (" + context + ")") +
                    "; 栈: " + SafeStackTrace());
            return false;
        }

        /// <summary>排空队列(由泵调用; 也可手动触发, 例如测试)。</summary>
        public static int DrainNow()
        {
            return _dispatcher.Drain();
        }

        // ---------- 泵 ----------

        private static void ScheduleFrame()
        {
            UniTask.Post(_frameCallback, PlayerLoopTiming.Update);
            UniTask.Post(_lateFrameCallback, PlayerLoopTiming.PostLateUpdate);
        }

        private static void OnFrame()
        {
            try
            {
                // 首次在 PlayerLoop 中执行 = 这就是主线程
                if (Interlocked.CompareExchange(ref _mainThreadId, Thread.CurrentThread.ManagedThreadId, 0) == 0)
                    SdkLog.Info("MAINTHREAD", "主线程 id 已确认: " + Thread.CurrentThread.ManagedThreadId);

                Interlocked.Increment(ref _frameCount);

                _dispatcher.Drain();
            }
            catch (Exception e)
            {
                SdkLog.Error("MAINTHREAD", "主线程帧处理异常: " + e.Message);
            }

            try { UpdateService.RaiseUpdate(); }
            catch (Exception e) { SdkLog.Error("MAINTHREAD", "Update 分发异常: " + e.Message); }

            if (Volatile.Read(ref _running) != 0)
            {
                try { UniTask.Post(_frameCallback, PlayerLoopTiming.Update); }
                catch (Exception e) { Volatile.Write(ref _running, 0); SdkLog.Warn("MAINTHREAD", "主线程泵停止: " + e.Message); }
            }
        }

        private static void OnLateFrame()
        {
            try { UpdateService.RaiseLateUpdate(); }
            catch (Exception e) { SdkLog.Error("MAINTHREAD", "LateUpdate 分发异常: " + e.Message); }

            if (Volatile.Read(ref _running) != 0)
            {
                try { UniTask.Post(_lateFrameCallback, PlayerLoopTiming.PostLateUpdate); }
                catch (Exception e) { Volatile.Write(ref _running, 0); SdkLog.Warn("MAINTHREAD", "主线程泵(Late)停止: " + e.Message); }
            }
        }

        // ---------- 辅助 ----------

        /// <summary>
        /// 尝试从 UniTask.PlayerLoopHelper 直接读取主线程 id, 让 IsMainThread 在第一帧之前就可用。
        /// 失败不影响功能(第一帧会自行确认)。
        /// </summary>
        private static void TryCaptureMainThreadIdFromUniTask()
        {
            try
            {
                // 直接读编译期引用的 UniTask 公开静态属性。
                //
                // 绝不要改成 Assembly.GetType(name, false): HybridCLR 的 AOT 裁剪会移除该 API,
                // 游戏内一调用就抛
                //     MissingMethodException: MethodNotFind System.Reflection.Assembly::GetType
                // 而且这类裁剪异常会穿透 try/catch, 把调用方 Initialize() 的后续步骤(pump 启动)
                // 一起带走 —— 离线的 net8.0 单测有完整 BCL, 永远发现不了这个问题。
                // (2026-09-20 游戏内实测: 4 个 mod 全部因此失效。)
                int id = Cysharp.Threading.Tasks.PlayerLoopHelper.MainThreadId;
                if (id > 0)
                {
                    Interlocked.CompareExchange(ref _mainThreadId, id, 0);
                    SdkLog.Debug("MAINTHREAD", "从 UniTask.PlayerLoopHelper 读到主线程 id: " + id);
                }
            }
            catch { }
        }

        private static void StartWatchdog()
        {
            try
            {
                _watchdog = new Timer(_ =>
                {
                    if (Volatile.Read(ref _frameCount) == 0 &&
                        Volatile.Read(ref _running) != 0 &&
                        Interlocked.Exchange(ref _warnedNoTick, 1) == 0)
                    {
                        SdkLog.Warn("MAINTHREAD",
                            "主线程泵 5 秒内未收到任何帧: UniTask PlayerLoop 可能未初始化; " +
                            "Unity API 调用会排队等待, 不会崩溃");
                    }
                }, null, 5000, 5000);
            }
            catch { }
        }

        private static string SafeStackTrace()
        {
            try { return new System.Diagnostics.StackTrace(1, false).ToString(); }
            catch { return "(不可用)"; }
        }
    }
}
