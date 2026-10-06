using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Mods;

namespace CesiumLoader.SDK.UserInterface
{
    /// <summary>
    /// 只在 UI 层使用的 ModContext 引用(避免 UI 命名空间直接依赖 Core 的具体实现细节)。
    /// </summary>
    public sealed class ModContextRef
    {
        internal ModContextRef(ModContext context)
        {
            Context = context;
        }

        /// <summary>被包装的上下文。</summary>
        public ModContext Context { get; private set; }

        /// <summary>mod 标识。</summary>
        public string ModId { get { return Context != null ? Context.ModId : null; } }

        /// <summary>文本描述。</summary>
        public override string ToString() { return ModId ?? "(未知 mod)"; }
    }
}
