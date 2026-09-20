using System;
using Cysharp.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// mod 生命周期基座。
    ///
    /// 两种用法, 可共存:
    ///
    /// 1) 旧式(保持完全兼容, 现有 mod 无需改动):
    ///    <code>ModBase.Run(init: OnInit, tick: OnTick, tag: "MyMod");</code>
    ///    等 delayMs(默认 30 秒)后执行 init; 传了 tick 则每秒执行一次, 不传则纯事件驱动不空转。
    ///
    /// 2) 新式(面向对象生命周期, 推荐新 mod 使用):
    ///    <code>
    ///    public sealed class MyMod : ModBase
    ///    {
    ///        public override void OnInitialize() { }
    ///        public override void OnUpdate() { }
    ///        public override void OnUnload() { }
    ///    }
    ///    // ModEntry.Main():
    ///    ModBase.Run(new MyMod());
    ///    </code>
    ///    新式会自动: 注册 ModContext / 主线程回调 / 场景事件, 并在卸载时自动退订全部订阅。
    ///
    /// 关键: 启动后先等 delayMs(默认 30 秒)再碰游戏单例(游戏启动早期访问
    /// NetManager/UIManager 等会触发 0x80000003 崩溃)。
    /// </summary>
    public abstract class ModBase : IMod
    {
        // =====================================================================
        // 旧 API —— 签名与行为保持不变, 保证已编译的 mod 继续工作
        // =====================================================================

        /// <summary>
        /// 启动 mod: 等 delayMs 后执行 init。若传了 tick, 之后每秒执行一次;
        /// 否则 init 完成后结束(事件驱动模式, 无轮询开销)。
        /// </summary>
        /// <param name="init">初始化(订阅事件/读配置)。可为 null。</param>
        /// <param name="tick">可选每秒轮询。null = 纯事件驱动, 无轮询。</param>
        /// <param name="delayMs">启动延迟, 默认 30 秒(避开游戏启动崩溃窗口)。</param>
        /// <param name="tag">日志来源标签。</param>
        public static void Run(Action init, Action tick = null, int delayMs = 30000, string tag = "MOD")
        {
            RunAsync(init, tick, delayMs, tag).Forget();
        }

        private static async UniTaskVoid RunAsync(Action init, Action tick, int delayMs, string tag)
        {
            SdkLog.Write(tag, "=== Main 被调用 ===");
            try
            {
                // 关键: 等待游戏完全启动再碰单例, 避免启动早期崩溃
                await UniTask.Delay(delayMs);
                SdkLog.Write(tag, "启动延迟结束, 开始初始化");

                // 新式路径依赖 ModContext; 旧式路径也顺带登记上下文, 让 Logger/Config 可用
                try { ModRegistry.GetOrRegister(System.Reflection.Assembly.GetCallingAssembly()); } catch { }

                Exception initError = null;
                MainThread.Run(() =>
                {
                    try { init?.Invoke(); }
                    catch (Exception e) { initError = e; }
                });
                if (initError != null) SdkLog.Write(tag, "init 异常: " + initError);

                // 事件驱动: 无 tick = init 完成后结束, 不空转
                if (tick == null) return;

                while (true)
                {
                    try { tick(); }
                    catch (Exception e) { SdkLog.Write(tag, "tick 异常: " + e.Message); }
                    await UniTask.Delay(1000);
                }
            }
            catch (Exception e)
            {
                SdkLog.Write(tag, "运行循环异常: " + e);
            }
        }

        // =====================================================================
        // 新 API —— 面向对象生命周期
        // =====================================================================

        /// <summary>
        /// 启动一个 <see cref="ModBase"/> 子类实例(新式生命周期)。
        /// 返回同一实例便于链式使用。
        /// </summary>
        /// <param name="mod">mod 实例。</param>
        /// <param name="delayMs">启动延迟, 默认 30 秒。</param>
        /// <param name="tag">日志标签, 默认用 mod 名。</param>
        public static T Run<T>(T mod, int delayMs = 30000, string tag = null) where T : ModBase
        {
            ModHost.Start(mod, delayMs, tag);
            return mod;
        }

        /// <summary>本 mod 的上下文(由程序集自动解析, 从未注册时为 null)。</summary>
        public ModContext Context
        {
            get
            {
                try { return ModRegistry.ForAssembly(GetType().Assembly) ?? ModRegistry.GetOrRegister(GetType().Assembly); }
                catch { return null; }
            }
        }

        /// <summary>当前生命周期状态。</summary>
        public ModLifecycleState State
        {
            get { var c = Context; return c != null ? c.State : ModLifecycleState.None; }
        }

        /// <summary>本 mod 的日志器(自动带上 mod 标签)。</summary>
        public ModLogger Log
        {
            get { var c = Context; return c != null ? c.Logger : new ModLogger(GetType().Name); }
        }

        /// <summary>本 mod 的配置(自动落盘到 mods\{ModId}\config.json)。</summary>
        public ModConfig Config
        {
            get { var c = Context; return c != null ? c.Config : null; }
        }

        /// <summary>显示名(默认类型名)。</summary>
        public virtual string Name { get { return GetType().Name; } }

        /// <summary>版本(默认 1.0.0; 若程序集带 [ModManifest] 建议与之一致)。</summary>
        public virtual string Version { get { return "1.0.0"; } }

        /// <summary>作者。</summary>
        public virtual string Author { get { return ""; } }

        /// <summary>描述。</summary>
        public virtual string Description { get { return ""; } }

        /// <summary>程序集加载后、启动延迟前(主线程)。此阶段不要访问游戏单例。</summary>
        public virtual void OnLoad() { }

        /// <summary>初始化(默认在启动延迟之后, 主线程执行)。</summary>
        public virtual void OnInitialize() { }

        /// <summary>场景加载完成(主线程)。</summary>
        public virtual void OnSceneLoaded(Scene scene, LoadSceneMode mode) { }

        /// <summary>场景卸载(主线程)。</summary>
        public virtual void OnSceneUnloaded(Scene scene) { }

        /// <summary>每帧(主线程)。仅当子类覆写时才注册, 不覆写则无开销。</summary>
        public virtual void OnUpdate() { }

        /// <summary>每帧 LateUpdate(主线程)。仅当子类覆写时才注册。</summary>
        public virtual void OnLateUpdate() { }

        /// <summary>IMGUI 绘制时机。仅当 UI 渲染后端可用时才会被调用。</summary>
        public virtual void OnGUI() { }

        /// <summary>卸载。SDK 已在调用前清理该 mod 的订阅/协程/UI。</summary>
        public virtual void OnUnload() { }
    }
}
