using System;
using System.Collections.Generic;
using System.Threading;

namespace CesiumLoader.SDK.Scheduling
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
}
