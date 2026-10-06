using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace CesiumLoader.SDK.Scheduling
{
    /// <summary>协程等待条件基类。</summary>
    public abstract class CoroutineYield
    {
        /// <summary>是否仍需等待。</summary>
        public abstract bool KeepWaiting { get; }
    }
}
