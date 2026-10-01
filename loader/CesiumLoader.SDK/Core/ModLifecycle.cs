using System;
using UnityEngine.SceneManagement;

namespace CesiumLoader.SDK
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

    /// <summary>
    /// 面向对象式 mod 生命周期接口(SDK 内部使用; mod 一般直接继承
    /// <see cref="CesiumLoader.SDK.ModBase"/>, 不必自己实现本接口)。
    ///
    /// 所有方法都有默认实现(空), mod 只覆写自己关心的阶段。
    /// </summary>
    public interface IMod
    {
        /// <summary>生命周期上下文(注册后由 SDK 注入)。</summary>
        ModContext Context { get; }

        /// <summary>显示名。</summary>
        string Name { get; }

        /// <summary>版本。</summary>
        string Version { get; }

        /// <summary>作者。</summary>
        string Author { get; }

        /// <summary>描述。</summary>
        string Description { get; }

        /// <summary>程序集加载后、初始化前(此阶段不要碰游戏单例)。</summary>
        void OnLoad();

        /// <summary>初始化(默认在启动延迟之后, 主线程上执行)。</summary>
        void OnInitialize();

        /// <summary>场景加载完成。</summary>
        void OnSceneLoaded(Scene scene, LoadSceneMode mode);

        /// <summary>场景卸载。</summary>
        void OnSceneUnloaded(Scene scene);

        /// <summary>每帧(主线程)。</summary>
        void OnUpdate();

        /// <summary>每帧 LateUpdate(主线程)。</summary>
        void OnLateUpdate();

        /// <summary>IMGUI 绘制时机(仅当 UI 后端可用时才会被调用)。</summary>
        void OnGUI();

        /// <summary>卸载: 此时 SDK 已自动清理该 mod 的订阅。</summary>
        void OnUnload();
    }
}
