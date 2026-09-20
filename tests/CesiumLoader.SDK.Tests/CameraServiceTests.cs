using System.IO;
using CesiumLoader.SDK;
using UnityEngine;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// CameraService 的解析策略与快照 API —— 通过 ICameraBackend 注入假后端,
    /// 因此完全脱离 Unity 运行时可测。
    /// 场景切换后的重新解析也在这里验证(缓存必须能失效)。
    /// </summary>
    public class CameraServiceTests
    {
        private static FakeCameraBackend Backend(params FakeCamera[] cameras)
        {
            var backend = new FakeCameraBackend();
            foreach (var cam in cameras) backend.Cameras.Add(cam);
            CameraService.SetBackend(backend);
            CameraService.InvalidateCaches();
            return backend;
        }

        private static FakeCamera Cam(string name, float depth = 0f, string tag = "Untagged", bool enabled = true)
        {
            return new FakeCamera { Name = name, Path = "/Root/" + name, Depth = depth, Tag = tag, Enabled = enabled };
        }

        [Fact]
        public void GetMainCamera_PrefersBackendMainCamera()
        {
            var main = Cam("SomeWeirdName", 10f, "MainCamera");
            var backend = Backend(Cam("Other", -5f), main);
            backend.Main = main;

            Assert.Same(main, CameraService.GetMainCameraHandle());
            Assert.True(CameraService.HasMainCamera);
        }

        [Fact]
        public void GetMainCamera_FallsBackToMainCameraTag()
        {
            var tagged = Cam("渲染相机", -5f, "MainCamera");
            Backend(Cam("Other", 1f), tagged);

            Assert.Same(tagged, CameraService.GetMainCameraHandle());
        }

        [Fact]
        public void GetMainCamera_PrefersEnabledCandidate_ButStillFallsBackToDisabled()
        {
            // 本游戏过渡期会把 Main Camera 临时失活。若同时存在一台启用的同名相机,
            // 应优先启用那台(避免接管到过渡相机); 若只有失活那台, 仍要能解析出来(旧行为兜底)。
            var disabled = Cam("Main Camera", 0f, "MainCamera", enabled: false);
            var enabled = Cam("Main Camera", -1f, "MainCamera", enabled: true);

            Backend(disabled, enabled);
            Assert.Same(enabled, CameraService.GetMainCameraHandle());

            Backend(disabled);
            Assert.Same(disabled, CameraService.GetMainCameraHandle());
        }

        [Fact]
        public void GetMainCamera_FallsBackToNameContainingMain()
        {
            var byName = Cam("PlayerMainView", 0.5f);
            Backend(Cam("Other", 9f), byName);

            Assert.Same(byName, CameraService.GetMainCameraHandle());
        }

        [Fact]
        public void GetMainCamera_LastResortUsesFrontCamera()
        {
            var front = Cam("Front", 5f);
            Backend(Cam("Back", 1f), front);

            Assert.Same(front, CameraService.GetMainCameraHandle());
        }

        [Fact]
        public void GetMainCamera_NoCamerasAtAll_ReturnsNullWithoutThrowing()
        {
            Backend();

            Assert.Null(CameraService.GetMainCameraHandle());
            Assert.False(CameraService.HasMainCamera);
        }

        [Fact]
        public void GetAllCameras_SortsByDepthDescendingAndSkipsDestroyed()
        {
            var dead = Cam("Dead", 100f, enabled: false);
            dead.Destroy();
            Backend(Cam("A", 1f), Cam("B", 3f), Cam("C", 2f), dead);

            var all = CameraService.GetAllCameraHandles(true);

            Assert.Equal(3, all.Length);
            Assert.Equal("B", ((FakeCamera)all[0]).Name);
            Assert.Equal("C", ((FakeCamera)all[1]).Name);
            Assert.Equal("A", ((FakeCamera)all[2]).Name);
        }

        [Fact]
        public void GetAllCameras_ExcludesDisabledWhenAsked()
        {
            Backend(Cam("On", 1f), Cam("Off", 2f, enabled: false));

            Assert.Equal(2, CameraService.GetAllCameraHandles(true).Length);
            Assert.Equal(1, CameraService.GetAllCameraHandles(false).Length);
        }

        [Fact]
        public void FindCamera_MatchesByNameThenPathThenPartial()
        {
            var target = Cam("PlayerCam");
            Backend(Cam("Other", 5f), target);

            Assert.Same(target, CameraService.FindCameraHandle("PlayerCam"));
            Assert.Same(target, CameraService.FindCameraHandle("playercam"));          // 大小写不敏感
            Assert.Same(target, CameraService.FindCameraHandle("/Root/PlayerCam"));    // 路径后缀
            Assert.Same(target, CameraService.FindCameraHandle("Player"));             // 包含匹配
            Assert.Null(CameraService.FindCameraHandle("NoSuchCamera"));
            Assert.Null(CameraService.FindCameraHandle(null));
        }

        [Fact]
        public void FindCamera_MainAlias_ResolvesMainCamera()
        {
            var main = Cam("Camera_Hero", 0f, "MainCamera");
            var backend = Backend(Cam("Zzz", 10f), main);
            backend.Main = main;

            Assert.Same(main, CameraService.FindCameraHandle("main"));
            Assert.Same(main, CameraService.FindCameraHandle("Main Camera"));
        }

        [Fact]
        public void CreateCamera_AddsCameraToScene_AndCanBeDestroyed()
        {
            var backend = Backend(Cam("Existing"));

            object created = CameraService.Backend.CreateCamera("CesiumFreeCamera", null);

            Assert.NotNull(created);
            Assert.Equal(2, backend.Cameras.Count);
            Assert.True(CameraService.IsCameraAlive(created));

            Assert.True(backend.DestroyCamera(created));
            Assert.False(CameraService.IsCameraAlive(created));
            Assert.Equal(1, CameraService.GetAllCameraHandles(true).Length);
        }

        [Fact]
        public void CaptureThenRestore_ViaServiceHandles_RestoresEverything()
        {
            var cam = Cam("Main", 0f, "MainCamera");
            cam.FieldOfView = 55f;
            cam.Position = new Vector3(4f, 5f, 6f);
            var backend = Backend(cam);
            backend.Main = cam;

            object handle = CameraService.GetMainCameraHandle();
            CameraState saved = CameraService.CaptureState(handle);
            Assert.True(saved.Valid);

            // 模拟 FreeCam 改动
            cam.FieldOfView = 100f;
            cam.Position = new Vector3(0f, 0f, 0f);

            Assert.True(CameraService.RestoreState(handle, saved));
            Assert.Equal(55f, cam.FieldOfView, 4);
            Assert.Equal(4f, cam.Position.x, 4);
        }

        [Fact]
        public void DestroyedCamera_InvalidatesCachedMainCamera()
        {
            var cam = Cam("Main", 0f, "MainCamera");
            var replacement = Cam("Camera_New", -1f, "MainCamera");
            var backend = Backend(cam);
            backend.Main = cam;

            Assert.Same(cam, CameraService.GetMainCameraHandle());

            // 场景切换: 旧相机销毁, 新相机出现
            cam.Destroy();
            backend.Main = replacement;
            backend.Cameras.Add(replacement);

            Assert.Same(replacement, CameraService.GetMainCameraHandle());
        }

        [Fact]
        public void InvalidateCaches_ClearsResolvedMainCamera()
        {
            var first = Cam("First", 0f, "MainCamera");
            var backend = Backend(first);

            Assert.Same(first, CameraService.GetMainCameraHandle());

            long hitsBefore = CameraService.CacheHitCount;
            Assert.Same(first, CameraService.GetMainCameraHandle());          // 命中缓存
            Assert.True(CameraService.CacheHitCount > hitsBefore);

            first.Destroy();
            var second = Cam("Second", 0f, "MainCamera");
            backend.Cameras.Add(second);
            CameraService.InvalidateCaches();

            Assert.Same(second, CameraService.GetMainCameraHandle());
        }

        [Fact]
        public void SerializeDeserialize_SurvivesRoundTrip()
        {
            var cam = Cam("Main", 0f, "MainCamera");
            cam.Aspect = 1.25f;
            var backend = Backend(cam);
            backend.Main = cam;

            string json = CameraService.SerializeState(CameraService.CaptureState(cam));

            CameraState parsed;
            Assert.True(CameraService.TryDeserializeState(json, out parsed));
            Assert.Equal(1.25f, parsed.Aspect, 4);

            CameraState ignored;
            Assert.False(CameraService.TryDeserializeState("{broken", out ignored));
        }

        [Fact]
        public void SaveAndLoadState_UsesFileSystem()
        {
            string dir = TestEnv.NewTempDir("camerastate");
            string path = Path.Combine(dir, "nested", "state.json");

            var cam = Cam("Main", 0f, "MainCamera");
            cam.FieldOfView = 71.5f;
            var backend = Backend(cam);
            backend.Main = cam;

            Assert.True(CameraService.SaveState(path, CameraService.CaptureState(cam)));
            Assert.True(File.Exists(path));

            CameraState loaded;
            Assert.True(CameraService.TryLoadState(path, out loaded));
            Assert.Equal(71.5f, loaded.FieldOfView, 4);

            CameraState missing;
            Assert.False(CameraService.TryLoadState(Path.Combine(dir, "nope.json"), out missing));
            Assert.False(CameraService.SaveState(null, loaded));
        }

        [Fact]
        public void ClearActiveCamera_IsSafeWithoutActiveRegistration()
        {
            Backend(Cam("Main", 0f, "MainCamera"));

            CameraService.ClearActiveCamera();

            Assert.NotNull(CameraService.GetActiveCameraHandle());   // 未显式设置时回落到主相机
        }
    }
}
