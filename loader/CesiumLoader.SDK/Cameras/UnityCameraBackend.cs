using System;
using System.Collections.Generic;
using UnityEngine;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Internals;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;

namespace CesiumLoader.SDK.Cameras
{
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
