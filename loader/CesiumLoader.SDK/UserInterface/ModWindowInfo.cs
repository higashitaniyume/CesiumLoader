using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.UserInterface
{
    /// <summary>Mod 窗口登记项(位置/大小仅作为渲染后端的建议值)。</summary>
    public sealed class ModWindowInfo
    {
        /// <summary>窗口标识(同一 mod 内唯一)。</summary>
        public string Id { get; internal set; }

        /// <summary>标题。</summary>
        public string Title { get; set; }

        /// <summary>归属 mod。</summary>
        public ModContextRef Owner { get; internal set; }

        /// <summary>是否打开。</summary>
        public bool IsOpen { get; internal set; }

        /// <summary>层级顺序(越大越靠前)。</summary>
        public int Order { get; internal set; }

        /// <summary>建议位置。</summary>
        public float X { get; set; }

        /// <summary>建议位置。</summary>
        public float Y { get; set; }

        /// <summary>建议宽度。</summary>
        public float Width { get; set; }

        /// <summary>建议高度。</summary>
        public float Height { get; set; }

        /// <summary>归属 mod 标识。</summary>
        public string OwnerId { get { return Owner != null ? Owner.ModId : null; } }

        /// <summary>文本描述。</summary>
        public override string ToString()
        {
            return "Window[" + Id + "] " + (Title ?? "") + (IsOpen ? " (打开)" : " (关闭)");
        }
    }
}
