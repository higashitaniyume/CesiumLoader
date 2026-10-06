using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.UserInterface
{
    /// <summary>Mod 覆盖层登记项(HUD 类常驻元素)。</summary>
    public sealed class ModOverlayInfo
    {
        /// <summary>覆盖层标识。</summary>
        public string Id { get; internal set; }

        /// <summary>归属 mod。</summary>
        public ModContextRef Owner { get; internal set; }

        /// <summary>是否可见。</summary>
        public bool IsVisible { get; internal set; }

        /// <summary>绘制顺序。</summary>
        public int Order { get; internal set; }

        /// <summary>归属 mod 标识。</summary>
        public string OwnerId { get { return Owner != null ? Owner.ModId : null; } }

        /// <summary>文本描述。</summary>
        public override string ToString()
        {
            return "Overlay[" + Id + "] " + (IsVisible ? "(可见)" : "(隐藏)");
        }
    }
}
