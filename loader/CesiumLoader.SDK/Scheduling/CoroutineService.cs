using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;

namespace CesiumLoader.SDK.Scheduling
{
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
