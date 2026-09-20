using System;
using CesiumLoader.SDK;
using UnityEngine;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 相机状态快照: 采集 / 完整还原 / JSON 序列化。
    /// 这是 FreeCamera "退出后必须完整还原" 这一保证的核心, 必须可离线验证。
    /// </summary>
    public class CameraStateTests
    {
        private static FakeCamera NewCamera()
        {
            return new FakeCamera
            {
                Name = "Main Camera",
                Tag = "MainCamera",
                Path = "/Root/Main Camera",
                Position = new Vector3(1f, 2f, 3f),
                Rotation = Quaternion.identity,
                FieldOfView = 62.5f,
                NearClipPlane = 0.2f,
                FarClipPlane = 1500f,
                Orthographic = false,
                OrthographicSize = 6f,
                Enabled = true,
                Depth = -1f,
                CullingMask = 12345,
                ClearFlags = 2,
                BackgroundColor = new Color(0.1f, 0.2f, 0.3f, 1f),
                Aspect = 1.777f,
            };
        }

        [Fact]
        public void Capture_InvalidInputs_ReturnsInvalidState()
        {
            var backend = new FakeCameraBackend();
            var dead = new FakeCamera();
            dead.Destroy();

            Assert.False(CameraState.Capture(null, dead).Valid);
            Assert.False(CameraState.Capture(backend, null).Valid);
            Assert.False(CameraState.Capture(backend, dead).Valid);
        }

        [Fact]
        public void Capture_ReadsEveryField()
        {
            var backend = new FakeCameraBackend();
            var cam = NewCamera();

            var state = CameraState.Capture(backend, cam);

            Assert.True(state.Valid);
            Assert.Equal("Main Camera", state.Name);
            Assert.Equal(1f, state.Position.x, 4);
            Assert.Equal(2f, state.Position.y, 4);
            Assert.Equal(3f, state.Position.z, 4);
            Assert.Equal(62.5f, state.FieldOfView, 4);
            Assert.Equal(0.2f, state.NearClipPlane, 4);
            Assert.Equal(1500f, state.FarClipPlane, 4);
            Assert.False(state.Orthographic);
            Assert.Equal(6f, state.OrthographicSize, 4);
            Assert.True(state.Enabled);
            Assert.Equal(-1f, state.Depth, 4);
            Assert.Equal(12345, state.CullingMask);
            Assert.Equal(2, state.ClearFlags);
            Assert.Equal(1.777f, state.Aspect, 3);
            Assert.Equal(0.1f, state.BackgroundColor.r, 3);
            Assert.False(string.IsNullOrEmpty(state.CapturedUtc));
        }

        [Fact]
        public void Restore_WritesBackEveryFieldThatWasCaptured()
        {
            var backend = new FakeCameraBackend();
            var cam = NewCamera();
            var state = CameraState.Capture(backend, cam);

            // 模拟自由相机把相机改坏
            cam.Position = new Vector3(99f, 99f, 99f);
            cam.Rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);   // 不能用 Quaternion.Euler: ECall 在测试进程里不可用
            cam.FieldOfView = 100f;
            cam.NearClipPlane = 5f;
            cam.FarClipPlane = 10f;
            cam.Orthographic = true;
            cam.OrthographicSize = 1f;
            cam.Enabled = false;
            cam.Depth = 50f;
            cam.CullingMask = 0;
            cam.ClearFlags = 0;
            cam.BackgroundColor = new Color(1f, 1f, 1f, 1f);
            cam.Aspect = 0.5f;

            backend.WriteCount = 0;
            Assert.True(CameraState.Restore(backend, cam, state));

            Assert.Equal(1f, cam.Position.x, 4);
            Assert.Equal(62.5f, cam.FieldOfView, 4);
            Assert.Equal(0.2f, cam.NearClipPlane, 4);
            Assert.Equal(1500f, cam.FarClipPlane, 4);
            Assert.False(cam.Orthographic);
            Assert.Equal(6f, cam.OrthographicSize, 4);
            Assert.True(cam.Enabled);
            Assert.Equal(-1f, cam.Depth, 4);
            Assert.Equal(12345, cam.CullingMask);
            Assert.Equal(2, cam.ClearFlags);
            Assert.Equal(0.1f, cam.BackgroundColor.r, 3);
            Assert.Equal(1.777f, cam.Aspect, 3);
            Assert.True(backend.WriteCount >= 13, "还原应把全部参数写回, 实际写入 " + backend.WriteCount + " 次");
        }

        [Fact]
        public void Restore_RejectsInvalidStateOrDeadCamera()
        {
            var backend = new FakeCameraBackend();
            var cam = NewCamera();
            var dead = new FakeCamera();
            dead.Destroy();

            Assert.False(CameraState.Restore(backend, cam, default(CameraState)));   // Valid=false
            Assert.False(CameraState.Restore(backend, dead, CameraState.Capture(backend, cam)));
            Assert.False(CameraState.Restore(null, cam, CameraState.Capture(backend, cam)));
        }

        [Fact]
        public void Json_RoundTripsAllScalars()
        {
            var backend = new FakeCameraBackend();
            var original = CameraState.Capture(backend, NewCamera());

            string json = original.ToJson();
            CameraState parsed;
            Assert.True(CameraState.TryParse(json, out parsed));

            Assert.True(parsed.Valid);
            Assert.Equal(original.Name, parsed.Name);
            Assert.Equal(original.PositionX, parsed.PositionX, 4);
            Assert.Equal(original.PositionY, parsed.PositionY, 4);
            Assert.Equal(original.PositionZ, parsed.PositionZ, 4);
            Assert.Equal(original.FieldOfView, parsed.FieldOfView, 4);
            Assert.Equal(original.NearClipPlane, parsed.NearClipPlane, 4);
            Assert.Equal(original.FarClipPlane, parsed.FarClipPlane, 4);
            Assert.Equal(original.Orthographic, parsed.Orthographic);
            Assert.Equal(original.OrthographicSize, parsed.OrthographicSize, 4);
            Assert.Equal(original.Enabled, parsed.Enabled);
            Assert.Equal(original.Depth, parsed.Depth, 4);
            Assert.Equal(original.CullingMask, parsed.CullingMask);
            Assert.Equal(original.ClearFlags, parsed.ClearFlags);
            Assert.Equal(original.Aspect, parsed.Aspect, 4);
            Assert.Equal(original.BackgroundR, parsed.BackgroundR, 4);
        }

        [Fact]
        public void Json_DoesNotDuplicateDerivedUnityProperties()
        {
            var backend = new FakeCameraBackend();
            string json = CameraState.Capture(backend, NewCamera()).ToJson();

            // Position/Rotation/EulerAngles/BackgroundColor 是标量之上的视图, 不能进 JSON
            Assert.DoesNotContain("\"Position\":", json);
            Assert.DoesNotContain("\"Rotation\":", json);
            Assert.DoesNotContain("\"EulerAngles\":", json);
            Assert.DoesNotContain("\"BackgroundColor\":", json);
            Assert.Contains("PositionX", json);
        }

        [Fact]
        public void TryParse_MalformedInput_ReturnsFalseNotThrow()
        {
            CameraState state;

            Assert.False(CameraState.TryParse(null, out state));
            Assert.False(CameraState.TryParse("", out state));
            Assert.False(CameraState.TryParse("{ not json", out state));
            Assert.False(CameraState.TryParse("[1,2,3]", out state));
        }

        [Fact]
        public void RestoredFromJson_StateCanBeWrittenBack()
        {
            var backend = new FakeCameraBackend();
            var cam = NewCamera();
            string json = CameraState.Capture(backend, cam).ToJson();

            CameraState parsed;
            Assert.True(CameraState.TryParse(json, out parsed));

            cam.FieldOfView = 12f;
            Assert.True(CameraState.Restore(backend, cam, parsed));
            Assert.Equal(62.5f, cam.FieldOfView, 4);
        }

        [Fact]
        public void Describe_MentionsKeyParameters()
        {
            var backend = new FakeCameraBackend();
            string text = CameraState.Capture(backend, NewCamera()).Describe();

            Assert.Contains("Main Camera", text);
            Assert.Contains("fov=62.5", text);
            Assert.Contains("CameraState(无效)", default(CameraState).Describe());
        }
    }
}
