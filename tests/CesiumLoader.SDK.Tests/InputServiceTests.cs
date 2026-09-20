using System;
using CesiumLoader.SDK;
using UnityEngine;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 输入服务: 全部通过 IInputBackend 注入, 因此键位/轴/鼠标行为可离线断言。
    /// 输入独占是 SDK 级"礼让"约定(不阻断游戏自身输入), 这里验证先到先得与自动释放。
    /// </summary>
    public class InputServiceTests : IDisposable
    {
        private readonly FakeInputBackend _backend = new FakeInputBackend();
        private readonly ModContext _owner;

        public InputServiceTests()
        {
            ModRegistry.Unregister("InputSvcMod");
            _owner = ModRegistry.Register("InputSvcMod", "1.0.0", "", "", typeof(InputServiceTests).Assembly);
            InputService.SetBackend(_backend);
            InputService.ForceReleaseAll();
        }

        public void Dispose()
        {
            InputService.ForceReleaseAll();
            InputService.SetBackend(null);
            ModRegistry.Unregister("InputSvcMod");
        }

        [Fact]
        public void KeyQueries_ReflectBackendState()
        {
            _backend.Down.Add(KeyCode.F1);
            _backend.Held.Add(KeyCode.W);
            _backend.Up.Add(KeyCode.Escape);

            Assert.True(InputService.IsKeyPressed(KeyCode.F1));
            Assert.True(InputService.IsKeyDown(KeyCode.F1));
            Assert.False(InputService.IsKeyHeld(KeyCode.F1));

            Assert.True(InputService.IsKeyHeld(KeyCode.W));
            Assert.False(InputService.IsKeyPressed(KeyCode.W));

            Assert.True(InputService.IsKeyReleased(KeyCode.Escape));
            Assert.True(InputService.IsKeyUp(KeyCode.Escape));

            Assert.False(InputService.IsKeyHeld(KeyCode.Z));
        }

        [Fact]
        public void IsAnyKeyDown_HandlesVariadicAndNull()
        {
            _backend.Down.Add(KeyCode.F2);

            Assert.True(InputService.IsAnyKeyDown(KeyCode.F1, KeyCode.F2));
            Assert.False(InputService.IsAnyKeyDown(KeyCode.F1, KeyCode.F3));
            Assert.False(InputService.IsAnyKeyDown(null));
        }

        [Fact]
        public void AxisQueries_AndMoveAxisClampsToUnitLength()
        {
            _backend.Axes["Horizontal"] = 1f;
            _backend.Axes["Vertical"] = 1f;

            Assert.Equal(1f, InputService.GetAxis("Horizontal"), 4);

            var move = InputService.GetMoveAxis();

            Assert.Equal(1f, move.magnitude, 3);          // 斜向不会超过 1
            Assert.Equal(0.707f, move.x, 3);
            Assert.Equal(0.707f, move.y, 3);
        }

        [Fact]
        public void MoveAxis_WithoutAxisData_IsZero()
        {
            var move = InputService.GetMoveAxis();

            Assert.Equal(0f, move.x);
            Assert.Equal(0f, move.y);
        }

        [Fact]
        public void MouseDelta_UsesUnityAxesWhenPresent()
        {
            _backend.Axes["Mouse X"] = 3.5f;
            _backend.Axes["Mouse Y"] = -1.25f;

            var delta = InputService.GetMouseDelta();

            Assert.Equal(3.5f, delta.x, 3);
            Assert.Equal(-1.25f, delta.y, 3);
        }

        [Fact]
        public void MouseScroll_AndPosition()
        {
            _backend.MouseScroll = new Vector2(0f, 2f);
            _backend.MousePosition = new Vector2(640f, 360f);

            Assert.Equal(2f, InputService.GetMouseScroll(), 3);

            Vector2 position;
            Assert.True(InputService.TryGetMousePosition(out position));
            Assert.Equal(640f, position.x, 3);
        }

        [Fact]
        public void MouseButtons_ReflectBackendState()
        {
            // 假后端用 KeyCode.Mouse0/1/2 表示鼠标键 (0=左, 1=右, 2=中)
            _backend.Down.Add(KeyCode.Mouse0);
            _backend.Held.Add(KeyCode.Mouse1);
            _backend.Up.Add(KeyCode.Mouse2);

            Assert.True(InputService.IsMouseButtonPressed(0));
            Assert.True(InputService.IsMouseButtonHeld(1));
            Assert.True(InputService.IsMouseButtonReleased(2));
            Assert.False(InputService.IsMouseButtonHeld(0));
        }

        [Fact]
        public void QueryCount_IncreasesWithEveryQuery()
        {
            long before = InputService.QueryCount;

            InputService.IsKeyHeld(KeyCode.A);
            InputService.GetAxis("Horizontal");
            InputService.GetMouseDelta();
            InputService.GetMouseScroll();

            Assert.True(InputService.QueryCount >= before + 4);
        }

        [Fact]
        public void BackendFailure_DegradesToFalseOrZero()
        {
            InputService.SetBackend(new ThrowingInputBackend());

            Assert.False(InputService.IsKeyHeld(KeyCode.A));
            Assert.False(InputService.IsKeyPressed(KeyCode.A));
            Assert.False(InputService.IsKeyReleased(KeyCode.A));
            Assert.Equal(0f, InputService.GetAxis("Horizontal"));
            Assert.Equal(0f, InputService.GetMouseScroll());
            Assert.False(InputService.TryGetMousePosition(out _));
            Assert.False(InputService.IsAvailable);

            var move = InputService.GetMoveAxis();
            Assert.Equal(0f, move.x);
            Assert.Equal(0f, move.y);
        }

        [Fact]
        public void SetBackendNull_RestoresDefaultCompositeBackend()
        {
            InputService.SetBackend(null);

            Assert.NotNull(InputService.Backend);
            Assert.False(string.IsNullOrEmpty(InputService.BackendName));
        }

        // ---------------- 独占 ----------------

        [Fact]
        public void CaptureKeyboard_IsFirstComeFirstServed()
        {
            ModRegistry.Unregister("InputSvcOther");
            var other = ModRegistry.Register("InputSvcOther", "1.0.0");

            Assert.False(InputService.IsKeyboardCaptured);
            Assert.True(InputService.CaptureKeyboard(other, false, "other"));
            Assert.Equal("InputSvcOther", InputService.KeyboardCaptureOwner);

            long deniedBefore = InputService.CaptureDeniedCount;
            Assert.False(InputService.CaptureKeyboard(_owner, false, "mine"));
            Assert.Equal("InputSvcOther", InputService.KeyboardCaptureOwner);
            Assert.Equal(deniedBefore + 1, InputService.CaptureDeniedCount);

            // force 可以抢过来
            Assert.True(InputService.CaptureKeyboard(_owner, true, "force"));
            Assert.Equal("InputSvcMod", InputService.KeyboardCaptureOwner);

            InputService.ForceReleaseAll();
            ModRegistry.Unregister("InputSvcOther");
        }

        [Fact]
        public void SameOwnerRecapturing_IsAlwaysAllowed()
        {
            Assert.True(InputService.CaptureKeyboard(_owner, false, "first"));
            Assert.True(InputService.CaptureKeyboard(_owner, false, "second"));

            Assert.Equal("InputSvcMod", InputService.KeyboardCaptureOwner);
        }

        [Fact]
        public void CaptureMouse_SetsOwnerAndReleasingClearsBoth()
        {
            Assert.True(InputService.Capture(_owner, true, true, false, "all"));
            Assert.True(InputService.IsKeyboardCaptured);
            Assert.True(InputService.IsMouseCaptured);
            Assert.True(InputService.IsInputCaptured);

            InputService.ReleaseAll(_owner);
            Assert.False(InputService.IsInputCaptured);
        }

        [Fact]
        public void NonOwnerCannotReleaseSomebodyElsesCapture()
        {
            ModRegistry.Unregister("InputSvcOther");
            var other = ModRegistry.Register("InputSvcOther", "1.0.0");
            InputService.CaptureKeyboard(other, false, "other");

            InputService.ReleaseKeyboard(_owner, false);      // 非持有者, 应被忽略
            Assert.Equal("InputSvcOther", InputService.KeyboardCaptureOwner);

            InputService.ReleaseKeyboard(_owner, true);        // force
            Assert.False(InputService.IsKeyboardCaptured);

            ModRegistry.Unregister("InputSvcOther");
        }

        [Fact]
        public void ForceReleaseAll_AlwaysWorks()
        {
            InputService.Capture(_owner, true, true, false, "x");

            InputService.ForceReleaseAll();

            Assert.False(InputService.IsInputCaptured);
            Assert.Null(InputService.KeyboardCaptureOwner);
            Assert.Null(InputService.MouseCaptureOwner);
        }

        [Fact]
        public void CaptureCounters_AreUpdated()
        {
            long grantedBefore = InputService.CaptureGrantedCount;

            InputService.CaptureKeyboard(_owner, false, "counted");

            Assert.Equal(grantedBefore + 1, InputService.CaptureGrantedCount);
        }

        [Fact]
        public void ModerateCapture_IsSafeOffline()
        {
            // 光标相关调用在无 Unity 运行时时必须被吞掉, 不影响独占登记
            InputService.SetCursorVisible(false);
            InputService.SetCursorLockMode(CursorLockMode.Locked);
            InputService.SaveAndLockCursor();

            Assert.NotNull(InputService.DescribeCursor());

            InputService.RestoreCursor();
            InputService.ForceReleaseAll();
        }

        [Fact]
        public void CaptureSelectionHelper_PicksRequestedKinds()
        {
            Assert.True(InputService.Capture(_owner, keyboard: true, mouse: false, reason: "kb-only"));
            Assert.True(InputService.IsKeyboardCaptured);
            Assert.False(InputService.IsMouseCaptured);

            InputService.ForceReleaseAll();
        }

        /// <summary>模拟输入后端整体失效 —— SDK 必须降级而不是抛异常。</summary>
        private sealed class ThrowingInputBackend : IInputBackend
        {
            public string Name { get { throw new Exception("no backend"); } }
            public bool IsAvailable { get { throw new Exception("no backend"); } }
            public bool GetKey(KeyCode key) { throw new Exception("boom"); }
            public bool GetKeyDown(KeyCode key) { throw new Exception("boom"); }
            public bool GetKeyUp(KeyCode key) { throw new Exception("boom"); }
            public float GetAxis(string axisName) { throw new Exception("boom"); }
            public bool GetMouseButton(int button) { throw new Exception("boom"); }
            public bool GetMouseButtonDown(int button) { throw new Exception("boom"); }
            public bool GetMouseButtonUp(int button) { throw new Exception("boom"); }
            public bool TryGetMousePosition(out Vector2 position) { throw new Exception("boom"); }
            public Vector2 GetMouseScrollDelta() { throw new Exception("boom"); }
            public bool TryGetMouseDelta(out Vector2 delta) { throw new Exception("boom"); }
        }
    }
}
