using System;
using UnityEngine.SceneManagement;

namespace CesiumLoader.SDK.Mods
{
    /// <summary>mod 生命周期状态。</summary>
    public enum ModLifecycleState
    {
        /// <summary>尚未注册。</summary>
        None = 0,
        /// <summary>已注册 / OnLoad 已执行。</summary>
        Loaded = 1,
        /// <summary>正在执行 OnInitialize。</summary>
        Initializing = 2,
        /// <summary>初始化完成, 正在接收 Update/事件。</summary>
        Initialized = 3,
        /// <summary>正在卸载(清理订阅)。</summary>
        Stopping = 4,
        /// <summary>已卸载。</summary>
        Stopped = 5,
        /// <summary>初始化或运行期失败。</summary>
        Failed = 6
    }
}
