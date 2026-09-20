using System;
using System.Collections.Generic;
using System.IO;
using CesiumLoader.SDK;
using UnityEngine;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>测试环境初始化: 把日志写到临时目录并关闭低级别输出。</summary>
    internal static class TestEnv
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Init()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "CesiumLoaderSdkTests", "logs");
                Directory.CreateDirectory(dir);
                Environment.SetEnvironmentVariable("CESIUM_LOG_DIR", dir);
                SdkLog.MinLevel = SdkLogLevel.Fatal;
            }
            catch { }
        }

        /// <summary>创建临时目录。</summary>
        internal static string NewTempDir(string hint)
        {
            string dir = Path.Combine(Path.GetTempPath(), "CesiumLoaderSdkTests", hint + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// 假相机: 纯托管数据, 不依赖 Unity 原生实现 —— 让相机逻辑可以脱离游戏测试。
    /// </summary>
    internal sealed class FakeCamera
    {
        public string Name = "Camera";
        public string Tag = "Untagged";
        public string Path = "/Camera";
        public bool Enabled = true;
        public bool Alive = true;
        public Vector3 Position = Vector3.zero;
        public Quaternion Rotation = Quaternion.identity;
        public float FieldOfView = 60f;
        public float NearClipPlane = 0.3f;
        public float FarClipPlane = 1000f;
        public bool Orthographic;
        public float OrthographicSize = 5f;
        public float Depth;
        public int CullingMask = -1;
        public int ClearFlags = 1;
        public Color BackgroundColor = new Color(0f, 0f, 0f, 0f);
        public float Aspect = 1.7777778f;

        /// <summary>模拟相机被销毁。</summary>
        public void Destroy() { Alive = false; }

        public override string ToString() { return Name + (Alive ? "" : "(已销毁)"); }
    }

    /// <summary>假相机后端。</summary>
    internal sealed class FakeCameraBackend : ICameraBackend
    {
        public readonly List<FakeCamera> Cameras = new List<FakeCamera>();
        public FakeCamera Main;

        /// <summary>记录被调用的写操作次数, 用于验证还原确实发生了。</summary>
        public int WriteCount;

        public bool IsAvailable { get { return true; } }

        public object GetMainCamera()
        {
            return Main != null && Main.Alive ? Main : null;
        }

        public object[] GetAllCameras(bool includeDisabled)
        {
            var list = new List<object>();
            var copy = new List<FakeCamera>(Cameras);
            copy.Sort((a, b) => b.Depth.CompareTo(a.Depth));
            foreach (var cam in copy)
            {
                if (!cam.Alive) continue;
                if (!includeDisabled && !cam.Enabled) continue;
                list.Add(cam);
            }
            return list.ToArray();
        }

        public bool IsAlive(object camera)
        {
            var fake = camera as FakeCamera;
            return fake != null && fake.Alive;
        }

        public string GetName(object camera) { var f = camera as FakeCamera; return f != null ? f.Name : null; }
        public string GetTag(object camera) { var f = camera as FakeCamera; return f != null ? f.Tag : null; }
        public string GetHierarchyPath(object camera) { var f = camera as FakeCamera; return f != null ? f.Path : null; }
        public object GetGameObject(object camera) { return camera; }
        public object GetTransform(object camera) { return camera; }

        public Vector3 GetPosition(object camera) { var f = camera as FakeCamera; return f != null ? f.Position : Vector3.zero; }
        public bool SetPosition(object camera, Vector3 value) { var f = camera as FakeCamera; if (f == null) return false; f.Position = value; WriteCount++; return true; }

        public Quaternion GetRotation(object camera) { var f = camera as FakeCamera; return f != null ? f.Rotation : Quaternion.identity; }
        public bool SetRotation(object camera, Quaternion value) { var f = camera as FakeCamera; if (f == null) return false; f.Rotation = value; WriteCount++; return true; }

        public Vector3 GetForward(object camera) { var f = camera as FakeCamera; return f != null ? f.Rotation * Vector3.forward : Vector3.forward; }
        public Vector3 GetRight(object camera) { var f = camera as FakeCamera; return f != null ? f.Rotation * Vector3.right : Vector3.right; }
        public Vector3 GetUp(object camera) { var f = camera as FakeCamera; return f != null ? f.Rotation * Vector3.up : Vector3.up; }

        public float GetFieldOfView(object camera) { var f = camera as FakeCamera; return f != null ? f.FieldOfView : 0f; }
        public bool SetFieldOfView(object camera, float value) { var f = camera as FakeCamera; if (f == null) return false; f.FieldOfView = value; WriteCount++; return true; }

        public float GetNearClipPlane(object camera) { var f = camera as FakeCamera; return f != null ? f.NearClipPlane : 0f; }
        public bool SetNearClipPlane(object camera, float value) { var f = camera as FakeCamera; if (f == null) return false; f.NearClipPlane = value; WriteCount++; return true; }

        public float GetFarClipPlane(object camera) { var f = camera as FakeCamera; return f != null ? f.FarClipPlane : 0f; }
        public bool SetFarClipPlane(object camera, float value) { var f = camera as FakeCamera; if (f == null) return false; f.FarClipPlane = value; WriteCount++; return true; }

        public bool GetOrthographic(object camera) { var f = camera as FakeCamera; return f != null && f.Orthographic; }
        public bool SetOrthographic(object camera, bool value) { var f = camera as FakeCamera; if (f == null) return false; f.Orthographic = value; WriteCount++; return true; }

        public float GetOrthographicSize(object camera) { var f = camera as FakeCamera; return f != null ? f.OrthographicSize : 0f; }
        public bool SetOrthographicSize(object camera, float value) { var f = camera as FakeCamera; if (f == null) return false; f.OrthographicSize = value; WriteCount++; return true; }

        public bool GetEnabled(object camera) { var f = camera as FakeCamera; return f != null && f.Enabled; }
        public bool SetEnabled(object camera, bool value) { var f = camera as FakeCamera; if (f == null) return false; f.Enabled = value; WriteCount++; return true; }

        public float GetDepth(object camera) { var f = camera as FakeCamera; return f != null ? f.Depth : 0f; }
        public bool SetDepth(object camera, float value) { var f = camera as FakeCamera; if (f == null) return false; f.Depth = value; WriteCount++; return true; }

        public int GetCullingMask(object camera) { var f = camera as FakeCamera; return f != null ? f.CullingMask : 0; }
        public bool SetCullingMask(object camera, int value) { var f = camera as FakeCamera; if (f == null) return false; f.CullingMask = value; WriteCount++; return true; }

        public int GetClearFlags(object camera) { var f = camera as FakeCamera; return f != null ? f.ClearFlags : 0; }
        public bool SetClearFlags(object camera, int value) { var f = camera as FakeCamera; if (f == null) return false; f.ClearFlags = value; WriteCount++; return true; }

        public Color GetBackgroundColor(object camera) { var f = camera as FakeCamera; return f != null ? f.BackgroundColor : default(Color); }
        public bool SetBackgroundColor(object camera, Color value) { var f = camera as FakeCamera; if (f == null) return false; f.BackgroundColor = value; WriteCount++; return true; }

        public float GetAspect(object camera) { var f = camera as FakeCamera; return f != null ? f.Aspect : 0f; }
        public bool SetAspect(object camera, float value) { var f = camera as FakeCamera; if (f == null) return false; f.Aspect = value; WriteCount++; return true; }

        public object CreateCamera(string name, object parent)
        {
            var fake = new FakeCamera { Name = name ?? "FakeCamera", Path = "/" + (name ?? "FakeCamera") };
            Cameras.Add(fake);
            return fake;
        }

        public bool DestroyCamera(object camera)
        {
            var fake = camera as FakeCamera;
            if (fake == null) return false;
            fake.Destroy();
            return true;
        }
    }

    /// <summary>假输入后端。</summary>
    internal sealed class FakeInputBackend : IInputBackend
    {
        public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
        public readonly HashSet<KeyCode> Down = new HashSet<KeyCode>();
        public readonly HashSet<KeyCode> Up = new HashSet<KeyCode>();
        public readonly Dictionary<string, float> Axes = new Dictionary<string, float>();
        public Vector2 MouseScroll = Vector2.zero;
        public Vector2 MousePosition = new Vector2(100f, 200f);
        public Vector2 MouseDelta = Vector2.zero;      // 光标位移回退(空 = 不提供)
        public bool MouseDeltaAvailable = true;
        public bool Available = true;

        public string Name { get { return "FakeInput"; } }
        public bool IsAvailable { get { return Available; } }

        public bool GetKey(KeyCode key) { return Held.Contains(key); }
        public bool GetKeyDown(KeyCode key) { return Down.Contains(key); }
        public bool GetKeyUp(KeyCode key) { return Up.Contains(key); }
        public bool GetMouseButton(int button) { return Held.Contains((KeyCode)((int)KeyCode.Mouse0 + button)); }
        public bool GetMouseButtonDown(int button) { return Down.Contains((KeyCode)((int)KeyCode.Mouse0 + button)); }
        public bool GetMouseButtonUp(int button) { return Up.Contains((KeyCode)((int)KeyCode.Mouse0 + button)); }

        public float GetAxis(string axisName)
        {
            float value;
            return Axes.TryGetValue(axisName, out value) ? value : 0f;
        }

        public bool TryGetMousePosition(out Vector2 position)
        {
            position = MousePosition;
            return true;
        }

        public Vector2 GetMouseScrollDelta() { return MouseScroll; }

        public bool TryGetMouseDelta(out Vector2 delta)
        {
            if (!MouseDeltaAvailable) { delta = Vector2.zero; return false; }
            delta = MouseDelta;
            return true;
        }
    }
}
