using System;
using System.Collections.Generic;
using System.Threading;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 生命周期与"卸载即清理"。这里验证的是需求里最硬的一条:
    /// mod 停止后, 它登记过的 Update / 场景 / UI / 事件订阅必须被清干净, 不留引用。
    /// </summary>
    public class ModLifecycleTests
    {
        private static ModContext NewContext(string id)
        {
            ModRegistry.Unregister(id);       // 保证测试独立
            return ModRegistry.Register(id, "1.2.3", "作者", "描述", typeof(ModLifecycleTests).Assembly);
        }

        // =====================================================================
        // ModContext
        // =====================================================================

        [Fact]
        public void ModContext_ExposesDeclaredMetadata()
        {
            var ctx = NewContext("LifecycleMetaMod");

            Assert.Equal("LifecycleMetaMod", ctx.ModId);
            Assert.Equal("1.2.3", ctx.Version);
            Assert.Equal("作者", ctx.Author);
            Assert.Equal("描述", ctx.Description);
            Assert.Equal(ModLifecycleState.Loaded, ctx.State);
            Assert.NotNull(ctx.Logger);
            Assert.NotNull(ctx.Config);
            Assert.False(ctx.IsCleaned);
            Assert.Equal("LifecycleMetaMod v1.2.3 [Loaded]", ctx.ToString());

            ModRegistry.Unregister(ctx.ModId);
        }

        [Fact]
        public void ModRegistry_RegisterIsIdempotentPerId()
        {
            var first = NewContext("LifecycleDupMod");
            var second = ModRegistry.Register("LifecycleDupMod", "9.9.9");

            Assert.Same(first, second);
            Assert.Equal("1.2.3", second.Version);

            ModRegistry.Unregister("LifecycleDupMod");
        }

        [Fact]
        public void ModRegistry_ResolvesByAssemblyAndId()
        {
            var ctx = NewContext("LifecycleLookupMod");
            var asm = typeof(ModLifecycleTests).Assembly;

            Assert.Same(ctx, ModRegistry.Get("LifecycleLookupMod"));
            Assert.Same(ctx, ModRegistry.ForAssembly(asm));
            Assert.Contains(ctx, ModRegistry.All);

            ModRegistry.Unregister("LifecycleLookupMod");
            Assert.Null(ModRegistry.Get("LifecycleLookupMod"));
        }

        [Fact]
        public void ModRegistry_SdkAssemblyIsNeverAMod()
        {
            Assert.Null(ModRegistry.ForAssembly(typeof(ModRegistry).Assembly));
            Assert.Null(ModRegistry.GetOrRegister(typeof(ModRegistry).Assembly));
            Assert.Null(ModRegistry.ForAssembly(null));
        }

        [Fact]
        public void Cleanup_RunsRegisteredActionsInReverseOrder()
        {
            var ctx = NewContext("LifecycleOrderMod");
            var order = new List<string>();

            ctx.RegisterCleanup(() => order.Add("first"));
            ctx.RegisterCleanup(() => order.Add("second"));
            ctx.RegisterCleanup(() => order.Add("third"));

            ctx.Cleanup();

            Assert.Equal(new[] { "third", "second", "first" }, order.ToArray());
            ModRegistry.Unregister("LifecycleOrderMod");
        }

        [Fact]
        public void Cleanup_IsIdempotent()
        {
            var ctx = NewContext("LifecycleIdempotentMod");
            int runs = 0;
            ctx.RegisterCleanup(() => runs++);

            ctx.Cleanup();
            ctx.Cleanup();
            ctx.Cleanup();

            Assert.Equal(1, runs);
            ModRegistry.Unregister("LifecycleIdempotentMod");
        }

        [Fact]
        public void Cleanup_RegistrationsAfterCleanupRunImmediately()
        {
            var ctx = NewContext("LifecycleLateMod");
            ctx.Cleanup();

            int runs = 0;
            ctx.RegisterCleanup(() => runs++);

            Assert.Equal(1, runs);
            Assert.True(ctx.IsCleaned);
            ModRegistry.Unregister("LifecycleLateMod");
        }

        [Fact]
        public void Cleanup_OneActionThrowingDoesNotStopOthers()
        {
            var ctx = NewContext("LifecycleThrowMod");
            var order = new List<string>();

            ctx.RegisterCleanup(() => order.Add("a"));
            ctx.RegisterCleanup(() => { throw new InvalidOperationException("boom"); });
            ctx.RegisterCleanup(() => order.Add("c"));

            ctx.Cleanup();

            Assert.Equal(new[] { "c", "a" }, order.ToArray());
            ModRegistry.Unregister("LifecycleThrowMod");
        }

        [Fact]
        public void Cleanup_CancelsCancellationTokenAndTransitionsState()
        {
            var ctx = NewContext("LifecycleCancelMod");
            Assert.False(ctx.CancellationToken.IsCancellationRequested);

            ctx.Cleanup();

            Assert.True(ctx.CancellationToken.IsCancellationRequested);
            Assert.Equal(ModLifecycleState.Stopped, ctx.State);
            ModRegistry.Unregister("LifecycleCancelMod");
        }

        [Fact]
        public void CleanupAll_StopsEveryRegisteredMod()
        {
            var a = NewContext("LifecycleAllA");
            var b = NewContext("LifecycleAllB");

            ModRegistry.CleanupAll();

            Assert.True(a.IsCleaned);
            Assert.True(b.IsCleaned);
            Assert.Equal(ModLifecycleState.Stopped, a.State);
            Assert.Equal(ModLifecycleState.Stopped, b.State);

            ModRegistry.Unregister(a.ModId);
            ModRegistry.Unregister(b.ModId);
        }

        // =====================================================================
        // 卸载自动清理各服务的订阅(需求 26)
        // =====================================================================

        [Fact]
        public void Cleanup_RemovesUpdateAndLateUpdateSubscriptions()
        {
            var ctx = NewContext("LifecycleUpdateCleanup");
            int updateBaseline = UpdateService.UpdateSubscriberCount;
            int lateBaseline = UpdateService.LateUpdateSubscriberCount;

            UpdateService.SubscribeUpdate(() => { }, ctx);
            UpdateService.SubscribeLateUpdate(() => { }, ctx);

            Assert.Equal(updateBaseline + 1, UpdateService.UpdateSubscriberCount);
            Assert.Equal(lateBaseline + 1, UpdateService.LateUpdateSubscriberCount);

            ctx.Cleanup();

            Assert.Equal(updateBaseline, UpdateService.UpdateSubscriberCount);
            Assert.Equal(lateBaseline, UpdateService.LateUpdateSubscriberCount);
            ModRegistry.Unregister("LifecycleUpdateCleanup");
        }

        [Fact]
        public void Cleanup_RemovesSceneSubscriptions()
        {
            var ctx = NewContext("LifecycleSceneCleanup");
            int loadedBaseline = SceneService.SceneLoadedSubscriberCount;
            int unloadedBaseline = SceneService.SceneUnloadedSubscriberCount;
            int activeBaseline = SceneService.ActiveSceneChangedSubscriberCount;

            SceneService.SubscribeSceneLoaded((s, m) => { }, ctx);
            SceneService.SubscribeSceneUnloaded(s => { }, ctx);
            SceneService.SubscribeActiveSceneChanged((p, c) => { }, ctx);

            Assert.Equal(loadedBaseline + 1, SceneService.SceneLoadedSubscriberCount);
            Assert.Equal(unloadedBaseline + 1, SceneService.SceneUnloadedSubscriberCount);
            Assert.Equal(activeBaseline + 1, SceneService.ActiveSceneChangedSubscriberCount);

            ctx.Cleanup();

            Assert.Equal(loadedBaseline, SceneService.SceneLoadedSubscriberCount);
            Assert.Equal(unloadedBaseline, SceneService.SceneUnloadedSubscriberCount);
            Assert.Equal(activeBaseline, SceneService.ActiveSceneChangedSubscriberCount);
            ModRegistry.Unregister("LifecycleSceneCleanup");
        }

        [Fact]
        public void Cleanup_RemovesUiRegistrations()
        {
            var ctx = NewContext("LifecycleUiCleanup");
            int overlayBaseline = UiService.OverlayCount;
            int windowBaseline = UiService.WindowCount;
            int guiBaseline = UiService.GuiCallbackCount;

            UiService.RegisterOverlay("overlay-" + ctx.ModId, true, ctx);
            UiService.OpenWindow("window-" + ctx.ModId, "标题", ctx);
            UiService.RegisterGuiCallback(() => { }, ctx, "gui");

            Assert.Equal(overlayBaseline + 1, UiService.OverlayCount);
            Assert.Equal(windowBaseline + 1, UiService.WindowCount);
            Assert.Equal(guiBaseline + 1, UiService.GuiCallbackCount);

            ctx.Cleanup();

            Assert.Equal(overlayBaseline, UiService.OverlayCount);
            Assert.Equal(windowBaseline, UiService.WindowCount);
            Assert.Equal(guiBaseline, UiService.GuiCallbackCount);
            ModRegistry.Unregister("LifecycleUiCleanup");
        }

        [Fact]
        public void Cleanup_ReleasesInputCaptureAndPlaysCursorBack()
        {
            var ctx = NewContext("LifecycleInputCleanup");

            Assert.True(InputService.CaptureKeyboard(ctx, false, "test"));
            Assert.True(InputService.IsKeyboardCaptured);
            Assert.Equal("LifecycleInputCleanup", InputService.KeyboardCaptureOwner);

            ctx.Cleanup();

            Assert.False(InputService.IsKeyboardCaptured);
            Assert.False(InputService.IsInputCaptured);
            ModRegistry.Unregister("LifecycleInputCleanup");
        }

        [Fact]
        public void SecondMod_CannotStealKeyboard_ButForceCan()
        {
            var a = NewContext("LifecycleInputA");
            var b = NewContext("LifecycleInputB");

            Assert.True(InputService.CaptureKeyboard(a, false, "a"));
            Assert.False(InputService.CaptureKeyboard(b, false, "b"));
            Assert.True(InputService.CaptureKeyboard(b, true, "b-force"));
            Assert.Equal("LifecycleInputB", InputService.KeyboardCaptureOwner);

            InputService.ForceReleaseAll();
            Assert.False(InputService.IsKeyboardCaptured);

            ModRegistry.Unregister(a.ModId);
            ModRegistry.Unregister(b.ModId);
        }

        [Fact]
        public void Cleanup_NullToken_IsSafe()
        {
            var ctx = NewContext("LifecycleNullToken");
            ctx.RegisterCleanup(null);

            ctx.Cleanup();

            Assert.True(ctx.IsCleaned);
            ModRegistry.Unregister("LifecycleNullToken");
        }

        // =====================================================================
        // ModBase 生命周期 API 表面
        // =====================================================================

        private sealed class MinimalMod : ModBase
        {
            public readonly List<string> Calls = new List<string>();
            public override void OnLoad() { Calls.Add("OnLoad"); }
            public override void OnInitialize() { Calls.Add("OnInitialize"); }
            public override void OnUpdate() { Calls.Add("OnUpdate"); }
            public override void OnUnload() { Calls.Add("OnUnload"); }
        }

        [Fact]
        public void ModBase_OnlyOverriddenMethodsNeedImplementing()
        {
            var mod = new MinimalMod();

            // 未覆写的生命周期方法必须是可安全调用的空实现
            mod.OnSceneLoaded(default(UnityEngine.SceneManagement.Scene), UnityEngine.SceneManagement.LoadSceneMode.Single);
            mod.OnSceneUnloaded(default(UnityEngine.SceneManagement.Scene));
            mod.OnLateUpdate();
            mod.OnGUI();

            mod.OnLoad();
            mod.OnInitialize();
            mod.OnUpdate();
            mod.OnUnload();

            Assert.Equal(new[] { "OnLoad", "OnInitialize", "OnUpdate", "OnUnload" }, mod.Calls.ToArray());
        }

        [Fact]
        public void ModBase_DefaultsAndContextResolution()
        {
            var ctx = NewContext("MinimalModAssemblyCtx");
            var mod = new MinimalMod();

            Assert.Equal("MinimalMod", mod.Name);
            Assert.Equal("1.0.0", mod.Version);
            Assert.Equal("", mod.Author);
            Assert.Equal("", mod.Description);

            // 测试程序集已注册 → ModBase 能解析到上下文, 并给出带标签的日志器/配置
            Assert.Same(ctx, mod.Context);
            Assert.NotNull(mod.Log);
            Assert.NotNull(mod.Config);
            Assert.Equal(ModLifecycleState.Loaded, mod.State);

            ModRegistry.Unregister("MinimalModAssemblyCtx");
        }

        [Fact]
        public void ModBase_OldRunApi_StillExists()
        {
            // 旧 API 必须保持可用(不调用它, 只验证签名没被删)
            var method = typeof(ModBase).GetMethod("Run",
                new[] { typeof(Action), typeof(Action), typeof(int), typeof(string) });

            Assert.NotNull(method);
        }
    }
}
