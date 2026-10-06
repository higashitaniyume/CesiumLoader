using System;
using System.Collections.Generic;
using System.Threading;

namespace CesiumLoader.SDK.UserInterface
{
    public sealed class UiState
    {
        /// <summary>是否安装了渲染后端。</summary>
        public bool RenderingAvailable;

        /// <summary>渲染后端名。</summary>
        public string BackendName;

        /// <summary>通知条数。</summary>
        public int NotificationCount;

        /// <summary>窗口总数。</summary>
        public int WindowCount;

        /// <summary>当前打开的窗口数。</summary>
        public int OpenWindowCount;

        /// <summary>覆盖层总数。</summary>
        public int OverlayCount;

        /// <summary>可见覆盖层数。</summary>
        public int VisibleOverlayCount;

        /// <summary>GUI 回调数。</summary>
        public int GuiCallbackCount;

        /// <summary>是否有 mod UI 打开(会屏蔽游戏输入)。</summary>
        public bool AnyUiOpen;

        /// <summary>打开的窗口名列表。</summary>
        public List<string> OpenWindows = new List<string>();

        /// <summary>文本描述。</summary>
        public override string ToString()
        {
            return "UI[rendering=" + RenderingAvailable + " backend=" + (BackendName ?? "(无)") +
                   " notify=" + NotificationCount + " windows=" + OpenWindowCount + "/" + WindowCount +
                   " overlays=" + VisibleOverlayCount + "/" + OverlayCount +
                   " gui=" + GuiCallbackCount + " blockingInput=" + AnyUiOpen + "]";
        }
    }
}
