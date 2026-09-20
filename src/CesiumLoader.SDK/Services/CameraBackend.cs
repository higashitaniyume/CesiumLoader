using System;
using System.Collections.Generic;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 相机后端抽象。
    ///
    /// 为什么需要它: CameraService 的"选相机 / 存状态 / 恢复状态 / 缓存失效"等逻辑
    /// 希望在<b>脱离游戏</b>的环境里单元测试。真实 Unity 相机无法在测试宿主里构造,
    /// 所以把"碰 Unity 的那一层"收敛到这个接口, 测试提供 <c>FakeCameraBackend</c> 即可。
    ///
    /// 约定: 相机用不透明 <c>object</c> 句柄表示; 所有方法必须空安全(传入 null/失效句柄
    /// 返回默认值, 不抛异常)。
    /// </summary>
    public interface ICameraBackend
    {
        /// <summary>后端是否可用(真实后端恒为 true)。</summary>
        bool IsAvailable { get; }

        /// <summary>等价 Camera.main; 没有则返回 null。</summary>
        object GetMainCamera();

        /// <summary>全部相机(按 depth 降序)。</summary>
        object[] GetAllCameras(bool includeDisabled);

        /// <summary>相机是否仍然存在(未销毁)。</summary>
        bool IsAlive(object camera);

        /// <summary>相机名字。</summary>
        string GetName(object camera);

        /// <summary>相机 tag。</summary>
        string GetTag(object camera);

        /// <summary>层级路径(诊断用)。</summary>
        string GetHierarchyPath(object camera);

        /// <summary>相机所在的 GameObject 句柄。</summary>
        object GetGameObject(object camera);

        /// <summary>相机所在 Transform 句柄。</summary>
        object GetTransform(object camera);

        // ---- 变换 ----
        Vector3 GetPosition(object camera);
        bool SetPosition(object camera, Vector3 value);
        Quaternion GetRotation(object camera);
        bool SetRotation(object camera, Quaternion value);
        Vector3 GetForward(object camera);
        Vector3 GetRight(object camera);
        Vector3 GetUp(object camera);

        // ---- 光学参数 ----
        float GetFieldOfView(object camera);
        bool SetFieldOfView(object camera, float value);
        float GetNearClipPlane(object camera);
        bool SetNearClipPlane(object camera, float value);
        float GetFarClipPlane(object camera);
        bool SetFarClipPlane(object camera, float value);
        bool GetOrthographic(object camera);
        bool SetOrthographic(object camera, bool value);
        float GetOrthographicSize(object camera);
        bool SetOrthographicSize(object camera, float value);

        // ---- 渲染/开关 ----
        bool GetEnabled(object camera);
        bool SetEnabled(object camera, bool value);
        float GetDepth(object camera);
        bool SetDepth(object camera, float value);
        int GetCullingMask(object camera);
        bool SetCullingMask(object camera, int value);
        int GetClearFlags(object camera);
        bool SetClearFlags(object camera, int value);
        Color GetBackgroundColor(object camera);
        bool SetBackgroundColor(object camera, Color value);
        float GetAspect(object camera);
        bool SetAspect(object camera, float value);

        // ---- 生命周期 ----
        /// <summary>创建相机(返回句柄; 失败返回 null)。<paramref name="parent"/> 可为 null 或句柄。</summary>
        object CreateCamera(string name, object parent);

        /// <summary>销毁相机。</summary>
        bool DestroyCamera(object camera);
    }

    /// <summary>
    /// 基于真实 UnityEngine.Camera 的后端。
    /// 所有读取都包了 try/catch, 并断言主线程。
    /// </summary>
    public sealed class UnityCameraBackend : ICameraBackend
    {
        /// <summary>可用。</summary>
        public bool IsAvailable { get { return true; } }

        /// <summary>Camera.main。</summary>
        public object GetMainCamera()
        {
            MainThread.AssertMainThread("CameraService.Main");
            try
            {
                var cam = UnityCall.MainCamera();
                return UnityObject.IsAlive(cam) ? cam : null;
            }
            catch { return null; }
        }

        /// <summary>全部相机(按 depth 降序)。</summary>
        public object[] GetAllCameras(bool includeDisabled)
        {
            MainThread.AssertMainThread("CameraService.GetAllCameras");
            var list = new List<Camera>(8);
            try
            {
                if (includeDisabled)
                {
                    list.AddRange(UnityObject.FindAll<Camera>(true));
                }
                else
                {
                    var arr = UnityCall.AllCameras();
                    if (arr != null) list.AddRange(arr);
                }
            }
            catch (Exception e)
            {
                SdkLog.Error("CAMERA", "枚举相机失败: " + e.Message);
            }

            list.Sort((a, b) => UnityCall.CameraDepth(b).CompareTo(UnityCall.CameraDepth(a)));

            var result = new object[list.Count];
            for (int i = 0; i < list.Count; i++) result[i] = list[i];
            return result;
        }

        /// <summary>判活。</summary>
        public bool IsAlive(object camera)
        {
            return camera is Camera cam && UnityObject.IsAlive(cam);
        }

        /// <summary>名字。</summary>
        public string GetName(object camera)
        {
            return camera is Camera cam ? UnityObject.GetName(cam) : null;
        }

        /// <summary>tag。</summary>
        public string GetTag(object camera)
        {
            return camera is Camera cam && UnityObject.IsAlive(cam) ? UnityCall.CameraTag(cam) : null;
        }

        /// <summary>层级路径。</summary>
        public string GetHierarchyPath(object camera)
        {
            return camera is Camera cam ? UnityObject.GetHierarchyPath(cam) : null;
        }

        /// <summary>GameObject。</summary>
        public object GetGameObject(object camera)
        {
            return camera is Camera cam ? UnityObject.GetGameObject(cam) : null;
        }

        /// <summary>Transform。</summary>
        public object GetTransform(object camera)
        {
            return camera is Camera cam ? UnityObject.GetTransform(cam) : null;
        }

        private static Transform T(object camera)
        {
            return camera is Camera cam && UnityObject.IsAlive(cam) ? UnityCall.TransformOf(cam) : null;
        }

        /// <summary>世界坐标。</summary>
        public Vector3 GetPosition(object camera) { return TransformService.GetPosition(T(camera)); }

        /// <summary>设置世界坐标。</summary>
        public bool SetPosition(object camera, Vector3 value) { return TransformService.SetPosition(T(camera), value); }

        /// <summary>世界旋转。</summary>
        public Quaternion GetRotation(object camera) { return TransformService.GetRotation(T(camera)); }

        /// <summary>设置世界旋转。</summary>
        public bool SetRotation(object camera, Quaternion value) { return TransformService.SetRotation(T(camera), value); }

        /// <summary>前方向量。</summary>
        public Vector3 GetForward(object camera) { return TransformService.GetForward(T(camera)); }

        /// <summary>右方向量。</summary>
        public Vector3 GetRight(object camera) { return TransformService.GetRight(T(camera)); }

        /// <summary>上方向量。</summary>
        public Vector3 GetUp(object camera) { return TransformService.GetUp(T(camera)); }

        private static Camera C(object camera)
        {
            var cam = camera as Camera;
            return UnityObject.IsAlive(cam) ? cam : null;
        }

        /// <summary>FOV。</summary>
        public float GetFieldOfView(object camera) { return UnityCall.FieldOfView(C(camera)); }

        /// <summary>设置 FOV。</summary>
        public bool SetFieldOfView(object camera, float value) { return UnityCall.SetFieldOfView(C(camera), value); }

        /// <summary>近裁剪面。</summary>
        public float GetNearClipPlane(object camera) { return UnityCall.NearClipPlane(C(camera)); }

        /// <summary>设置近裁剪面。</summary>
        public bool SetNearClipPlane(object camera, float value) { return UnityCall.SetNearClipPlane(C(camera), value); }

        /// <summary>远裁剪面。</summary>
        public float GetFarClipPlane(object camera) { return UnityCall.FarClipPlane(C(camera)); }

        /// <summary>设置远裁剪面。</summary>
        public bool SetFarClipPlane(object camera, float value) { return UnityCall.SetFarClipPlane(C(camera), value); }

        /// <summary>是否正交。</summary>
        public bool GetOrthographic(object camera) { return UnityCall.CameraOrthographic(C(camera)); }

        /// <summary>设置正交。</summary>
        public bool SetOrthographic(object camera, bool value) { return UnityCall.SetCameraOrthographic(C(camera), value); }

        /// <summary>正交尺寸。</summary>
        public float GetOrthographicSize(object camera) { return UnityCall.OrthographicSize(C(camera)); }

        /// <summary>设置正交尺寸。</summary>
        public bool SetOrthographicSize(object camera, float value) { return UnityCall.SetOrthographicSize(C(camera), value); }

        /// <summary>是否启用。</summary>
        public bool GetEnabled(object camera) { return UnityCall.CameraEnabled(C(camera)); }

        /// <summary>设置启用。</summary>
        public bool SetEnabled(object camera, bool value) { return UnityCall.SetCameraEnabled(C(camera), value); }

        /// <summary>depth。</summary>
        public float GetDepth(object camera) { return UnityCall.CameraDepth(C(camera)); }

        /// <summary>设置 depth。</summary>
        public bool SetDepth(object camera, float value) { return UnityCall.SetCameraDepth(C(camera), value); }

        /// <summary>剔除遮罩。</summary>
        public int GetCullingMask(object camera) { return UnityCall.CullingMask(C(camera)); }

        /// <summary>设置剔除遮罩。</summary>
        public bool SetCullingMask(object camera, int value) { return UnityCall.SetCullingMask(C(camera), value); }

        /// <summary>清屏标志(枚举底层值)。</summary>
        public int GetClearFlags(object camera) { return UnityCall.ClearFlags(C(camera)); }

        /// <summary>设置清屏标志。</summary>
        public bool SetClearFlags(object camera, int value) { return UnityCall.SetClearFlags(C(camera), value); }

        /// <summary>背景色。</summary>
        public Color GetBackgroundColor(object camera) { return UnityCall.BackgroundColor(C(camera)); }

        /// <summary>设置背景色。</summary>
        public bool SetBackgroundColor(object camera, Color value) { return UnityCall.SetBackgroundColor(C(camera), value); }

        /// <summary>宽高比。</summary>
        public float GetAspect(object camera) { return UnityCall.Aspect(C(camera)); }

        /// <summary>设置宽高比。</summary>
        public bool SetAspect(object camera, float value) { return UnityCall.SetAspect(C(camera), value); }

        /// <summary>创建相机对象。</summary>
        public object CreateCamera(string name, object parent)
        {
            try
            {
                Transform parentTransform = parent as Transform;
                return GameObjectUtil.CreateCameraObject(
                    string.IsNullOrEmpty(name) ? "CesiumCamera" : name, parentTransform);
            }
            catch (Exception e)
            {
                SdkLog.Error("CAMERA", "创建相机失败: " + e.Message);
                return null;
            }
        }

        /// <summary>销毁相机对象。</summary>
        public bool DestroyCamera(object camera)
        {
            var go = GetGameObject(camera);
            if (go is GameObject gameObject) return UnityObject.SafeDestroy(gameObject);
            return camera is UnityEngine.Object obj && UnityObject.SafeDestroy(obj);
        }
    }
}
