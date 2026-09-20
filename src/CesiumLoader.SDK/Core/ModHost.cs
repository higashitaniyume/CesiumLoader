using System;
using System.Reflection;
using Cysharp.Threading.Tasks;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 生命周期驱动器: 把一个 <see cref="IMod"/> 从 OnLoad 推进到 OnUnload,
    /// 并在这个过程中完成"主线程保证 + 订阅登记 + 卸载自动清理"。
    ///
    /// 线程约定:
    ///  - OnLoad/OnInitialize/OnUpdate/OnLateUpdate/OnSceneLoaded/OnUnload 全部在主线程执行。
    ///  - 启动延迟用 UniTask.Delay(与既有 ModBase.Run 相同机制)从 boot 线程切回主线程。
    /// </summary>
    public static class ModHost
    {
        /// <summary>默认启动延迟(与旧 API 一致)。</summary>
        public const int DefaultDelayMs = 30000;

        /// <summary>启动一个 mod(不阻塞调用者)。</summary>
        public static void Start(IMod mod, int delayMs = DefaultDelayMs, string tag = null)
        {
            if (mod == null) return;

            Type type = mod.GetType();
            string label = string.IsNullOrEmpty(tag) ? (mod.Name ?? type.Name) : tag;

            ModContext ctx = null;
            try { ctx = ModRegistry.GetOrRegister(type.Assembly); }
            catch (Exception e) { SdkLog.Error(label, "注册 mod 上下文失败: " + e.Message); }

            if (ctx == null)
            {
                try { ctx = ModRegistry.Register(type.Name, mod.Version, mod.Author, mod.Description, type.Assembly); }
                catch (Exception e) { SdkLog.Error(label, "创建 mod 上下文失败: " + e.Message); }
            }

            SdkLog.Info(label, "生命周期开始: " + (mod.Name ?? type.Name) +
                              " v" + mod.Version + " (启动延迟 " + delayMs + "ms)");

            RunLifecycleAsync(mod, ctx, delayMs, label).Forget();
        }

        /// <summary>手动卸载一个已启动的 mod。</summary>
        public static void Stop(IMod mod, string tag = null)
        {
            if (mod == null) return;
            string label = string.IsNullOrEmpty(tag) ? (mod.Name ?? mod.GetType().Name) : tag;

            var ctx = mod.Context;
            if (ctx != null && ctx.State == ModLifecycleState.Stopped)
            {
                SdkLog.Debug(label, "已卸载, 忽略重复 Stop");
                return;
            }

            SdkLog.Info(label, "开始卸载");
            if (ctx != null) ctx.Cleanup();          // 自动退订事件/Update/协程/UI/对象
            SafeOnMain(label, "OnUnload", mod.OnUnload);
            if (ctx != null) ctx.State = ModLifecycleState.Stopped;
            SdkLog.Info(label, "卸载完成");
        }

        private static async UniTaskVoid RunLifecycleAsync(IMod mod, ModContext ctx, int delayMs, string label)
        {
            // ---- OnLoad: 主线程, 立即执行(不等待启动延迟) ----
            ctx.State = ModLifecycleState.Loaded;
            SafeOnMain(label, "OnLoad", mod.OnLoad);

            // ---- 启动延迟: 避开游戏启动早期的崩溃窗口 ----
            if (delayMs > 0)
            {
                try { await UniTask.Delay(delayMs); }
                catch (Exception e) { SdkLog.Warn(label, "启动延迟异常(继续初始化): " + e.Message); }
            }

            // ---- OnInitialize: 主线程 ----
            ctx.State = ModLifecycleState.Initializing;

            Exception initError = null;
            MainThread.Run(() =>
            {
                try { mod.OnInitialize(); }
                catch (Exception e) { initError = e; }
            });

            if (initError != null)
            {
                ctx.LastError = initError;
                ctx.State = ModLifecycleState.Failed;
                SdkLog.ReportCrash(label, "OnInitialize", initError);
                ctx.Cleanup();
                SafeOnMain(label, "OnUnload(初始化失败)", mod.OnUnload);
                return;
            }

            // ---- 注册每帧回调 / 场景事件 / GUI(只注册真正覆写了的) ----
            try
            {
                Type type = mod.GetType();

                if (Overrides(type, "OnUpdate"))
                    UpdateService.SubscribeUpdate(mod.OnUpdate, ctx, label + ".OnUpdate");

                if (Overrides(type, "OnLateUpdate"))
                    UpdateService.SubscribeLateUpdate(mod.OnLateUpdate, ctx, label + ".OnLateUpdate");

                bool wantSceneLoaded = Overrides(type, "OnSceneLoaded");
                bool wantSceneUnloaded = Overrides(type, "OnSceneUnloaded");
                if (wantSceneLoaded)
                    SceneService.SubscribeSceneLoaded(mod.OnSceneLoaded, ctx);
                if (wantSceneUnloaded)
                    SceneService.SubscribeSceneUnloaded(mod.OnSceneUnloaded, ctx);

                if (Overrides(type, "OnGUI"))
                    UiService.RegisterGuiCallback(mod.OnGUI, ctx, label + ".OnGUI");
            }
            catch (Exception e)
            {
                ctx.LastError = e;
                ctx.State = ModLifecycleState.Failed;
                SdkLog.ReportCrash(label, "注册回调", e);
                ctx.Cleanup();
                return;
            }

            ctx.State = ModLifecycleState.Initialized;
            SdkLog.Info(label, "初始化完成 (状态: Initialized)");
        }

        /// <summary>
        /// 判断子类是否覆写了某个生命周期方法(避免给空实现装每帧回调)。
        /// 非 ModBase 的 IMod 实现一律视为已覆写。
        /// </summary>
        private static bool Overrides(Type type, string methodName)
        {
            try
            {
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (method == null) return false;
                return method.DeclaringType != typeof(ModBase);
            }
            catch { return true; }
        }

        private static void SafeOnMain(string label, string what, Action action)
        {
            if (action == null) return;
            MainThread.Run(() =>
            {
                try { action(); }
                catch (Exception e) { SdkLog.ReportCrash(label, what, e); }
            });
        }
    }
}
