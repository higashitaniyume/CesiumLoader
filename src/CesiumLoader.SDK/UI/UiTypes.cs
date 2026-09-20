using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK
{
    /// <summary>通知级别。</summary>
    public enum UiNotificationLevel
    {
        /// <summary>普通信息。</summary>
        Info = 0,
        /// <summary>成功。</summary>
        Success = 1,
        /// <summary>警告。</summary>
        Warning = 2,
        /// <summary>错误。</summary>
        Error = 3
    }

    /// <summary>一条 Mod 通知。</summary>
    public struct ModNotification
    {
        /// <summary>自增 id。</summary>
        public long Id;

        /// <summary>发起 mod。</summary>
        public string ModId;

        /// <summary>文本。</summary>
        public string Text;

        /// <summary>级别。</summary>
        public UiNotificationLevel Level;

        /// <summary>创建时刻(realtimeSinceStartup)。</summary>
        public float CreatedAt;

        /// <summary>存活秒数(&lt;=0 表示直到手动清除)。</summary>
        public float Ttl;

        /// <summary>是否已过期。</summary>
        public bool IsExpired;

        /// <summary>剩余秒数。</summary>
        public float Remaining(float now)
        {
            if (Ttl <= 0f) return float.MaxValue;
            return Math.Max(0f, Ttl - (now - CreatedAt));
        }

        /// <summary>文本描述。</summary>
        public override string ToString()
        {
            return "[" + Level + "] " + (ModId ?? "?") + ": " + Text;
        }
    }

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
