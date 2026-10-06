using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Manifests;

namespace CesiumLoader.SDK.Diagnostics
{
    public sealed class RuntimeDump
    {
        /// <summary>生成时刻(UTC)。</summary>
        public string CapturedUtc;

        /// <summary>SDK 版本。</summary>
        public string SdkVersion;

        /// <summary>Unity 版本。</summary>
        public string UnityVersion;

        /// <summary>游戏版本。</summary>
        public string GameVersion;

        /// <summary>产品名。</summary>
        public string ProductName;

        /// <summary>是否 Windows。</summary>
        public bool IsWindows;

        /// <summary>已加载程序集数量。</summary>
        public int AssemblyCount;

        /// <summary>主线程 id(0 = 未确认)。</summary>
        public int MainThreadId;

        /// <summary>主线程泵是否运行。</summary>
        public bool MainThreadPumpRunning;

        /// <summary>已泵出的帧数。</summary>
        public int FrameCount;

        /// <summary>主线程队列积压。</summary>
        public int MainThreadQueueLength;

        /// <summary>主线程累计执行数。</summary>
        public long MainThreadExecuted;

        /// <summary>主线程累计失败数。</summary>
        public long MainThreadFailed;

        /// <summary>Update 订阅数。</summary>
        public int UpdateSubscribers;

        /// <summary>LateUpdate 订阅数。</summary>
        public int LateUpdateSubscribers;

        /// <summary>活跃协程数。</summary>
        public int ActiveCoroutines;

        /// <summary>场景订阅数。</summary>
        public int SceneSubscribers;

        /// <summary>输入后端描述。</summary>
        public string InputBackend;

        /// <summary>输入是否可用。</summary>
        public bool InputAvailable;

        /// <summary>键盘独占者。</summary>
        public string KeyboardCapture;

        /// <summary>鼠标独占者。</summary>
        public string MouseCapture;

        /// <summary>UI 是否具备渲染能力。</summary>
        public bool UiRenderingAvailable;

        /// <summary>是否有 mod UI 打开。</summary>
        public bool UiOpen;

        /// <summary>Cinemachine 是否可用。</summary>
        public bool CinemachineAvailable;

        /// <summary>IL2CPP 互操作层是否就绪(GameAssembly.dll 导出齐全)。</summary>
        public bool Il2CppAvailable;

        /// <summary>当前线程是否已挂接到 IL2CPP 域。</summary>
        public bool Il2CppAttached;

        /// <summary>IL2CPP 互操作单行描述(含 domain 指针与程序集数)。</summary>
        public string Il2CppDetail;

        /// <summary>HybridCLR 热更程序集(AstralParty.Runtime)是否已加载。</summary>
        public bool RuntimeAssemblyLoaded;

        /// <summary>已注册 mod 数量。</summary>
        public int ModCount;

        /// <summary>已注册 mod 列表。</summary>
        public List<string> Mods = new List<string>();

        /// <summary>已加载程序集名(截断到前 80 个)。</summary>
        public List<string> Assemblies = new List<string>();

        /// <summary>多行文本。</summary>
        public string ToText()
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("===== CesiumLoader RuntimeDump =====");
            sb.AppendLine("时间(UTC)      : " + CapturedUtc);
            sb.AppendLine("SDK 版本       : " + SdkVersion);
            sb.AppendLine("Unity 版本     : " + UnityVersion + "   游戏版本: " + GameVersion + "   产品: " + ProductName);
            sb.AppendLine("主线程         : id=" + MainThreadId + " 泵运行=" + MainThreadPumpRunning +
                          " 帧数=" + FrameCount + " 队列=" + MainThreadQueueLength +
                          " 执行=" + MainThreadExecuted + " 失败=" + MainThreadFailed);
            sb.AppendLine("程序集         : " + AssemblyCount + " 个已加载");
            sb.AppendLine("每帧回调       : Update=" + UpdateSubscribers + " LateUpdate=" + LateUpdateSubscribers);
            sb.AppendLine("协程/场景订阅  : 协程=" + ActiveCoroutines + " 场景=" + SceneSubscribers);
            sb.AppendLine("输入           : 可用=" + InputAvailable + " 后端=" + InputBackend);
            sb.AppendLine("输入独占       : 键盘=" + (KeyboardCapture ?? "(无)") + " 鼠标=" + (MouseCapture ?? "(无)"));
            sb.AppendLine("UI             : 渲染=" + UiRenderingAvailable + " 打开=" + UiOpen);
            sb.AppendLine("Cinemachine    : 可用=" + CinemachineAvailable);
            sb.AppendLine("IL2CPP         : 就绪=" + Il2CppAvailable + " 线程已挂接=" + Il2CppAttached);
            sb.AppendLine("IL2CPP 明细    : " + (Il2CppDetail ?? "(未采集)"));
            sb.AppendLine("HybridCLR      : AstralParty.Runtime 已加载=" + RuntimeAssemblyLoaded);
            sb.AppendLine("已注册 Mod     : " + ModCount + " 个");
            for (int i = 0; i < Mods.Count; i++) sb.AppendLine("  - " + Mods[i]);
            return sb.ToString();
        }

        /// <summary>JSON 文本。</summary>
        public string ToJson()
        {
            return CesiumJson.SerializePretty(this);
        }

        /// <summary>摘要。</summary>
        public override string ToString() { return ToText(); }
    }
}
