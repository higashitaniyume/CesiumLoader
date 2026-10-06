using System;
using System.Collections.Generic;
using UnityEngine;

namespace CesiumLoader.SDK.Cameras
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
}
