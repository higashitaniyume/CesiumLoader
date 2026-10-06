using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CesiumLoader.SDK.Scenes
{
    /// <summary>场景订阅句柄。</summary>
    public sealed class SceneSubscription : IDisposable
    {
        private Action _dispose;

        internal SceneSubscription(Action dispose) { _dispose = dispose; }

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
