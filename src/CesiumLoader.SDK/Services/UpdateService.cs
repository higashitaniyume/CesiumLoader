using System;
using System.Collections.Generic;
using System.Threading;
namespace CesiumLoader.SDK
{
    /// <summary>订阅句柄: Dispose 即退订(可安全 Dispose 多次)。</summary>
    public sealed class UpdateSubscription : IDisposable
    {
        private Action _dispose;
        private readonly long _id;

        internal UpdateSubscription(long id, Action dispose)
        {
            _id = id;
            _dispose = dispose;
        }

        /// <summary>订阅序号。</summary>
        public long Id { get { return _id; } }

        /// <summary>是否已退订。</summary>
        public bool IsDisposed { get { return _dispose == null; } }

        /// <summary>退订。</summary>
        public void Dispose()
        {
            var d = Interlocked.Exchange(ref _dispose, null);
            if (d != null) { try { d(); } catch { } }
        }
    }

    /// <summary>
    /// 每帧回调服务(主线程)。
    ///
    /// 设计要点(针对"不能在 mod 里挂 MonoBehaviour"这一现实):
    ///  - 不需要 mod 创建 MonoBehaviour / GameObject; 回调由 SDK 的主线程泵驱动。
    ///  - 订阅可按 mod 归属, mod 卸载时自动全部退订(通过 <see cref="ModContext.RegisterCleanup"/>)。
    ///  - 遍历使用 copy-on-write 快照数组, 每帧零分配, 且在回调中增删订阅不会破坏遍历。
    ///  - 单个回调抛异常只记录, 不影响其它 mod。
    /// </summary>
    public static class UpdateService
    {
        private struct Entry
        {
            public long Id;
            public Action Callback;
            public ModContext Owner;
            public string Tag;
        }

        private static readonly object _lock = new object();
        private static readonly List<Entry> _updateList = new List<Entry>();
        private static readonly List<Entry> _lateList = new List<Entry>();
        private static Entry[] _updateSnapshot = new Entry[0];
        private static Entry[] _lateSnapshot = new Entry[0];
        private static long _nextId;
        private static long _updateTicks;
        private static long _lateTicks;
        private static long _exceptions;

        /// <summary>Update 订阅数。</summary>
        public static int UpdateSubscriberCount { get { return Volatile.Read(ref _updateSnapshot).Length; } }

        /// <summary>LateUpdate 订阅数。</summary>
        public static int LateUpdateSubscriberCount { get { return Volatile.Read(ref _lateSnapshot).Length; } }

        /// <summary>累计触发的 Update 次数。</summary>
        public static long UpdateTicks { get { return Interlocked.Read(ref _updateTicks); } }

        /// <summary>累计触发的 LateUpdate 次数。</summary>
        public static long LateUpdateTicks { get { return Interlocked.Read(ref _lateTicks); } }

        /// <summary>回调抛异常累计次数。</summary>
        public static long ExceptionCount { get { return Interlocked.Read(ref _exceptions); } }

        // ---------- 订阅 ----------

        /// <summary>
        /// 订阅每帧 Update(主线程执行)。
        /// <paramref name="owner"/> 为 null 时会尝试用调用方程序集自动解析 mod 上下文,
        /// 以便 mod 卸载时自动退订。
        /// </summary>
        public static UpdateSubscription SubscribeUpdate(Action callback, ModContext owner = null, string tag = null)
        {
            return Subscribe(_updateList, callback, owner, tag ?? "UPDATE", true);
        }

        /// <summary>订阅每帧 LateUpdate(主线程执行)。</summary>
        public static UpdateSubscription SubscribeLateUpdate(Action callback, ModContext owner = null, string tag = null)
        {
            return Subscribe(_lateList, callback, owner, tag ?? "LATEUPDATE", false);
        }

        private static UpdateSubscription Subscribe(List<Entry> list, Action callback, ModContext owner, string tag, bool isUpdate)
        {
            if (callback == null) return new UpdateSubscription(0, null);

            var resolvedOwner = owner ?? SafeCurrentContext();

            long id;
            lock (_lock)
            {
                id = ++_nextId;
                list.Add(new Entry { Id = id, Callback = callback, Owner = resolvedOwner, Tag = tag });
                RebuildSnapshotsLocked();
            }

            if (resolvedOwner != null)
            {
                // mod 卸载时自动退订: 用 id 精确移除, 避免误删同名委托
                resolvedOwner.RegisterCleanup(() => RemoveById(id));
            }

            return new UpdateSubscription(id, () => RemoveById(id));
        }

        // ---------- 退订 ----------

        /// <summary>按委托退订 Update(移除所有匹配项)。</summary>
        public static bool UnsubscribeUpdate(Action callback)
        {
            return RemoveByCallback(_updateList, callback);
        }

        /// <summary>按委托退订 LateUpdate。</summary>
        public static bool UnsubscribeLateUpdate(Action callback)
        {
            return RemoveByCallback(_lateList, callback);
        }

        /// <summary>移除指定 mod 的全部每帧回调(等价于需求里的 RemoveAllUpdateCallbacks)。</summary>
        public static int RemoveAllUpdateCallbacks(ModContext owner)
        {
            if (owner == null) return 0;
            int removed = 0;
            lock (_lock)
            {
                removed += _updateList.RemoveAll(e => e.Owner == owner);
                removed += _lateList.RemoveAll(e => e.Owner == owner);
                RebuildSnapshotsLocked();
            }
            return removed;
        }

        /// <summary>清空全部订阅(SDK 关停/测试用)。</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _updateList.Clear();
                _lateList.Clear();
                RebuildSnapshotsLocked();
            }
        }

        // ---------- 触发(由主线程泵调用) ----------

        /// <summary>SDK 内部: 触发 Update。</summary>
        public static void RaiseUpdate()
        {
            Interlocked.Increment(ref _updateTicks);
            InvokeAll(Volatile.Read(ref _updateSnapshot));

            try { UpdateEvents.RaiseUpdate(); }
            catch (Exception e) { SdkLog.ReportCrash("UPDATE", "UpdateEvents", e); }
        }

        /// <summary>SDK 内部: 触发 LateUpdate。</summary>
        public static void RaiseLateUpdate()
        {
            Interlocked.Increment(ref _lateTicks);
            InvokeAll(Volatile.Read(ref _lateSnapshot));

            try { UpdateEvents.RaiseLateUpdate(); }
            catch (Exception e) { SdkLog.ReportCrash("UPDATE", "UpdateEvents(Late)", e); }
        }

        // ---------- 内部 ----------

        private static void InvokeAll(Entry[] snapshot)
        {
            for (int i = 0; i < snapshot.Length; i++)
            {
                var entry = snapshot[i];
                try { entry.Callback(); }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _exceptions);
                    SdkLog.ReportCrash(entry.Tag ?? "UPDATE", "每帧回调", e);
                }
            }
        }

        private static void RemoveById(long id)
        {
            lock (_lock)
            {
                _updateList.RemoveAll(e => e.Id == id);
                _lateList.RemoveAll(e => e.Id == id);
                RebuildSnapshotsLocked();
            }
        }

        private static bool RemoveByCallback(List<Entry> list, Action callback)
        {
            if (callback == null) return false;
            lock (_lock)
            {
                int removed = list.RemoveAll(e => e.Callback == callback);
                if (removed > 0) RebuildSnapshotsLocked();
                return removed > 0;
            }
        }

        private static void RebuildSnapshotsLocked()
        {
            Volatile.Write(ref _updateSnapshot, _updateList.ToArray());
            Volatile.Write(ref _lateSnapshot, _lateList.ToArray());
        }

        private static ModContext SafeCurrentContext()
        {
            try { return ModContext.Current; }
            catch { return null; }
        }
    }
}
