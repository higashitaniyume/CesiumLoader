using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
namespace CesiumLoader.SDK
{
    /// <summary>协程等待条件基类。</summary>
    public abstract class CoroutineYield
    {
        /// <summary>是否仍需等待。</summary>
        public abstract bool KeepWaiting { get; }
    }

    /// <summary>等待若干秒(使用实时时间, 不受 Time.timeScale 影响)。</summary>
    public sealed class RoutineWaitSeconds : CoroutineYield
    {
        private readonly float _seconds;
        private float _deadline = float.NaN;

        /// <summary>构造。</summary>
        public RoutineWaitSeconds(float seconds) { _seconds = seconds < 0f ? 0f : seconds; }

        /// <summary>剩余秒数。</summary>
        public float Remaining
        {
            get
            {
                if (float.IsNaN(_deadline)) return _seconds;
                float now = SafeNow();
                return Math.Max(0f, _deadline - now);
            }
        }

        /// <summary>是否仍需等待。</summary>
        public override bool KeepWaiting
        {
            get
            {
                if (float.IsNaN(_deadline)) _deadline = SafeNow() + _seconds;
                return SafeNow() < _deadline;
            }
        }

        private static float SafeNow()
        {
            return UnityCall.RealtimeSinceStartup();
        }
    }

    /// <summary>等待若干帧。</summary>
    public sealed class RoutineWaitFrames : CoroutineYield
    {
        private int _remaining;

        /// <summary>构造。</summary>
        public RoutineWaitFrames(int frames) { _remaining = frames < 0 ? 0 : frames; }

        /// <summary>剩余帧数。</summary>
        public int Remaining { get { return _remaining; } }

        /// <summary>是否仍需等待。</summary>
        public override bool KeepWaiting
        {
            get
            {
                if (_remaining <= 0) return false;
                _remaining--;
                return _remaining > 0;
            }
        }
    }

    /// <summary>等待直到条件为真。</summary>
    public sealed class RoutineWaitUntil : CoroutineYield
    {
        private readonly Func<bool> _predicate;

        /// <summary>构造。</summary>
        public RoutineWaitUntil(Func<bool> predicate) { _predicate = predicate; }

        /// <summary>是否仍需等待。</summary>
        public override bool KeepWaiting
        {
            get
            {
                if (_predicate == null) return false;
                try { return !_predicate(); }
                catch { return false; }
            }
        }
    }

    /// <summary>等待直到条件为假。</summary>
    public sealed class RoutineWaitWhile : CoroutineYield
    {
        private readonly Func<bool> _predicate;

        /// <summary>构造。</summary>
        public RoutineWaitWhile(Func<bool> predicate) { _predicate = predicate; }

        /// <summary>是否仍需等待。</summary>
        public override bool KeepWaiting
        {
            get
            {
                if (_predicate == null) return false;
                try { return _predicate(); }
                catch { return false; }
            }
        }
    }

    /// <summary>协程句柄: 可查询状态、Stop 停止、Dispose 等价于 Stop。</summary>
    public sealed class CoroutineHandle : IDisposable
    {
        private Action _stop;

        internal CoroutineHandle(long id, string name, Action stop)
        {
            Id = id;
            Name = name;
            _stop = stop;
        }

        /// <summary>协程序号。</summary>
        public long Id { get; private set; }

        /// <summary>协程名(诊断用)。</summary>
        public string Name { get; private set; }

        /// <summary>是否已结束(正常结束或被停止)。</summary>
        public bool IsDone { get; internal set; }

        /// <summary>是否被显式停止。</summary>
        public bool IsStopped { get; internal set; }

        /// <summary>最近一次异常(没有则 null)。</summary>
        public Exception LastError { get; internal set; }

        /// <summary>停止协程。</summary>
        public void Stop()
        {
            var stop = Interlocked.Exchange(ref _stop, null);
            if (stop != null) { try { stop(); } catch { } }
        }

        /// <summary>等价于 Stop。</summary>
        public void Dispose() { Stop(); }

        /// <summary>文本描述。</summary>
        public override string ToString()
        {
            return "Coroutine#" + Id + "(" + (Name ?? "?") + ")" + (IsDone ? "[done]" : "");
        }
    }

    /// <summary>
    /// 协程服务。
    ///
    /// 为什么自己实现: mod 运行在热更程序集里, 无法可靠地给 GameObject 挂
    /// MonoBehaviour 来获得 Unity 的 StartCoroutine(而且那也要求主线程 + 生命周期管理)。
    /// 这里用 SDK 的主线程泵做调度 —— 与 Unity 协程的写法一致:
    ///
    /// <code>
    /// CoroutineService.StartCoroutine(MyRoutine());
    ///
    /// IEnumerator MyRoutine()
    /// {
    ///     SdkLog.Info("MOD", "开始");
    ///     yield return new RoutineWaitSeconds(1.5f);
    ///     yield return null;                       // 等一帧
    ///     yield return new RoutineWaitFrames(10);
    ///     yield return new RoutineWaitUntil(() => Players.SelfValid());
    ///     SdkLog.Info("MOD", "结束");
    /// }
    /// </code>
    ///
    /// 支持嵌套协程(yield return 另一个 IEnumerator)。
    /// 所有推进都在主线程; 单个协程抛异常只影响自身。
    /// </summary>
    public static class CoroutineService
    {
        private sealed class Routine
        {
            public long Id;
            public string Name;
            public ModContext Owner;
            public CoroutineHandle Handle;
            public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
            public CoroutineYield Waiting;
        }

        private static readonly object _lock = new object();
        private static readonly List<Routine> _routines = new List<Routine>();
        private static Routine[] _snapshot = new Routine[0];
        private static long _nextId;
        private static int _installed;
        private static long _started;
        private static long _completed;
        private static long _exceptions;

        /// <summary>当前活跃协程数。</summary>
        public static int ActiveCount { get { lock (_lock) { return _routines.Count; } } }

        /// <summary>累计启动协程数。</summary>
        public static long StartedCount { get { return Interlocked.Read(ref _started); } }

        /// <summary>累计正常结束协程数。</summary>
        public static long CompletedCount { get { return Interlocked.Read(ref _completed); } }

        /// <summary>协程异常累计次数。</summary>
        public static long ExceptionCount { get { return Interlocked.Read(ref _exceptions); } }

        /// <summary>
        /// 启动一个协程(内部会推进到主线程帧上, 可从任意线程调用)。
        /// </summary>
        /// <param name="routine">协程体。</param>
        /// <param name="owner">归属 mod(卸载时自动停止)。</param>
        /// <param name="name">名字(诊断用)。</param>
        public static CoroutineHandle StartCoroutine(IEnumerator routine, ModContext owner = null, string name = null)
        {
            if (routine == null) return null;

            EnsureInstalled();
            var resolvedOwner = owner ?? SafeCurrentContext();

            var entry = new Routine
            {
                Name = name ?? ("Routine" + (_nextId + 1)),
                Owner = resolvedOwner
            };

            lock (_lock)
            {
                entry.Id = ++_nextId;
                _routines.Add(entry);
                RebuildSnapshotLocked();
            }

            entry.Stack.Push(routine);
            entry.Handle = new CoroutineHandle(entry.Id, entry.Name, () => StopById(entry.Id));

            Interlocked.Increment(ref _started);

            if (resolvedOwner != null)
                resolvedOwner.RegisterCleanup(() => StopById(entry.Id));

            return entry.Handle;
        }

        /// <summary>启动协程(便捷重载)。</summary>
        public static CoroutineHandle Start(IEnumerator routine, ModContext owner = null, string name = null)
        {
            return StartCoroutine(routine, owner, name);
        }

        /// <summary>停止指定协程。</summary>
        public static bool Stop(CoroutineHandle handle)
        {
            if (handle == null) return false;
            handle.Stop();
            return true;
        }

        /// <summary>停止某个 mod 的全部协程(等价需求里的 StopAllCoroutines)。</summary>
        public static int StopAllCoroutines(ModContext owner)
        {
            if (owner == null) return 0;

            Routine[] toStop;
            lock (_lock)
            {
                var list = new List<Routine>();
                foreach (var routine in _routines)
                {
                    if (routine.Owner == owner) list.Add(routine);
                }
                toStop = list.ToArray();
            }

            for (int i = 0; i < toStop.Length; i++) StopById(toStop[i].Id);
            return toStop.Length;
        }

        /// <summary>停止全部协程(SDK 关停/测试用)。</summary>
        public static void StopAll()
        {
            Routine[] all;
            lock (_lock) { all = _routines.ToArray(); }
            for (int i = 0; i < all.Length; i++) StopById(all[i].Id);
        }

        /// <summary>由主线程泵调用: 推进所有协程一帧。</summary>
        public static void Tick()
        {
            var snapshot = Volatile.Read(ref _snapshot);
            for (int i = 0; i < snapshot.Length; i++)
            {
                Advance(snapshot[i]);
            }
        }

        // =====================================================================
        // 内部
        // =====================================================================

        private static void EnsureInstalled()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;
            try { UpdateService.SubscribeUpdate(Tick, null, "CoroutineService.Tick"); }
            catch (Exception e) { SdkLog.Warn("COROUTINE", "注册协程调度失败: " + e.Message); }
        }

        private static void Advance(Routine routine)
        {
            if (routine == null || routine.Handle == null || routine.Handle.IsDone) return;

            try
            {
                // 1) 等待条件
                if (routine.Waiting != null)
                {
                    bool keep;
                    try { keep = routine.Waiting.KeepWaiting; }
                    catch { keep = false; }

                    if (keep) return;
                    routine.Waiting = null;
                }

                // 2) 推进(可能一次走过多层已结束的嵌套协程)
                int guard = 0;
                while (routine.Stack.Count > 0 && guard++ < 64)
                {
                    var current = routine.Stack.Peek();

                    bool moved;
                    try { moved = current.MoveNext(); }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref _exceptions);
                        routine.Handle.LastError = e;
                        SdkLog.ReportCrash(routine.Owner != null ? routine.Owner.ModId : "COROUTINE",
                            "协程 " + routine.Name, e);
                        Finish(routine, false);
                        return;
                    }

                    if (!moved)
                    {
                        routine.Stack.Pop();
                        if (routine.Stack.Count == 0)
                        {
                            Finish(routine, true);
                            return;
                        }
                        continue; // 子协程结束, 继续推进父协程
                    }

                    object yielded = current.Current;

                    if (yielded == null) return;                                  // 等一帧

                    var wait = yielded as CoroutineYield;
                    if (wait != null) { routine.Waiting = wait; return; }

                    var nested = yielded as IEnumerator;
                    if (nested != null) { routine.Stack.Push(nested); continue; }  // 嵌套协程

                    return; // 其它值按"等一帧"处理
                }

                if (routine.Stack.Count == 0) Finish(routine, true);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _exceptions);
                routine.Handle.LastError = e;
                SdkLog.ReportCrash(routine.Owner != null ? routine.Owner.ModId : "COROUTINE",
                    "协程调度 " + routine.Name, e);
                Finish(routine, false);
            }
        }

        private static void Finish(Routine routine, bool completed)
        {
            if (completed) Interlocked.Increment(ref _completed);
            if (routine.Handle != null)
            {
                routine.Handle.IsDone = true;
                if (routine.Handle.LastError != null) routine.Handle.IsStopped = false;
            }

            lock (_lock)
            {
                _routines.RemoveAll(r => r.Id == routine.Id);
                RebuildSnapshotLocked();
            }
        }

        private static void StopById(long id)
        {
            Routine target = null;
            lock (_lock)
            {
                foreach (var routine in _routines)
                {
                    if (routine.Id == id) { target = routine; break; }
                }
            }

            if (target == null) return;

            if (target.Handle != null)
            {
                target.Handle.IsDone = true;
                target.Handle.IsStopped = true;
            }

            lock (_lock)
            {
                _routines.RemoveAll(r => r.Id == id);
                RebuildSnapshotLocked();
            }
        }

        private static void RebuildSnapshotLocked()
        {
            Volatile.Write(ref _snapshot, _routines.ToArray());
        }

        private static ModContext SafeCurrentContext()
        {
            try { return ModContext.Current; }
            catch { return null; }
        }
    }
}
