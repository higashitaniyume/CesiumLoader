using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.UserInterface
{
    /// <summary>
    /// UI 后端抽象: 真正"画到屏幕上"的那一层。
    ///
    /// 为什么需要它: 本 SDK 编译期只引用 UnityEngine.CoreModule,
    /// 而 IMGUI(UnityEngine.GUI)位于 UnityEngine.IMGUIModule —— 无法直接调用。
    /// 因此渲染交给可插拔后端:
    ///  - 不安装后端时 UI 服务仍然完整可用(登记/通知/层级/输入屏蔽/诊断),
    ///    通知同时写入日志, 便于无渲染环境下观察。
    ///  - 可用 <see cref="CesiumLoader.SDK.UiService.TryInstallBackend"/> 安装自定义后端
    ///    (例如对接游戏自身的 FairyGUI)。
    /// </summary>
    public interface IUiBackend
    {
        /// <summary>后端名(诊断用)。</summary>
        string Name { get; }

        /// <summary>是否可用。</summary>
        bool IsAvailable { get; }

        /// <summary>安装(只调用一次); 返回是否成功。</summary>
        bool Install();

        /// <summary>卸载。</summary>
        void Uninstall();

        /// <summary>每帧绘制通知(由后端在合适的渲染时机调用)。</summary>
        void DrawNotifications(IList<ModNotification> notifications);

        /// <summary>每帧绘制窗口。</summary>
        void DrawWindows(IList<ModWindowInfo> windows);

        /// <summary>每帧绘制覆盖层。</summary>
        void DrawOverlays(IList<ModOverlayInfo> overlays);
    }
}
