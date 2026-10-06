using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace CesiumLoader.SDK.Scheduling
{
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
}
