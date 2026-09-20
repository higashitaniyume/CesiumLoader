using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 相机服务。
    ///
    /// 解析顺序(绝不假设主相机叫 "Main Camera"):
    ///   1. <c>Camera.main</c>(Unity 认定的主相机, 需要 tag=MainCamera 且启用)
    ///   2. 场景中 tag == "MainCamera" 的相机
    ///   3. 名字里包含 "main" 的相机(不区分大小写)
    ///   4. depth 最高的启用相机
    ///
    /// 缓存策略: 解析结果会被缓存, 但每次使用前都用 IsAlive 校验(Unity 对象可能被销毁),
    /// 并在场景切换时整体失效 —— 因为切场景会销毁旧相机。
    ///
    /// ⚠️ 重要(Cinemachine 现实): 本游戏的主相机由 CinemachineBrain 驱动,
    /// <b>直接写 Camera.main.transform 会被 brain 每帧覆盖</b>。
    /// 需要自由相机时请用 <see cref="CinemachineService"/> 提升一个 VirtualCamera 的优先级,
    /// 或先禁用 brain(见 CinemachineService.DisableBrain)。
    ///
    /// 可测试性: 所有 Unity 访问都经 <see cref="ICameraBackend"/>; 单元测试可替换为假后端。
    /// </summary>
    public static class CameraService
    {
        private static ICameraBackend _backend = new UnityCameraBackend();
        private static int _installed;

        private static object _cachedMain;
        private static object _activeCamera;

        private static long _resolveCount;
        private static long _cacheHits;

        // =====================================================================
        // 后端
        // =====================================================================

        /// <summary>当前后端(默认真实 Unity 后端)。</summary>
        public static ICameraBackend Backend { get { return _backend; } }

        /// <summary>替换后端(测试/自定义管线用)。传 null 恢复默认。</summary>
        public static void SetBackend(ICameraBackend backend)
        {
            _backend = backend ?? new UnityCameraBackend();
            InvalidateCaches();
        }

        /// <summary>相机解析次数(诊断/性能观察用)。</summary>
        public static long ResolveCount { get { return Interlocked.Read(ref _resolveCount); } }

        /// <summary>相机缓存命中次数。</summary>
        public static long CacheHitCount { get { return Interlocked.Read(ref _cacheHits); } }

        // =====================================================================
        // 查询
        // =====================================================================

        /// <summary>是否存在可用主相机。</summary>
        public static bool HasMainCamera
        {
            get { return GetMainCameraHandle() != null; }
        }

        /// <summary>
        /// 取主相机句柄(按上述顺序解析)。返回 null 表示当前没有相机。
        ///
        /// 解析顺序在本游戏实测后做了细化: <b>优先挑启用中的相机, 但保留"含失活相机"的兜底</b>。
        /// 原因是本游戏在菜单/场景过渡期会把 Main Camera 临时失活(enabled=false, 停在极高的
        /// 过渡位置); 若只认启用相机, 过渡期就会返回 null 甚至退化成"随便一台启用的相机"
        /// (可能是 UI 相机)。因此顺序为:
        ///   1) Camera.main → 2) tag=MainCamera(启用中) → 3) 名字含 main(启用中)
        ///   → 4) tag=MainCamera(含失活, 兼容过渡期) → 5) 名字含 main(含失活)
        ///   → 6) depth 最高的启用相机
        /// "这台相机能不能接管"由调用方判断(见自由相机的就绪校验); SDK 只负责给最合适的候选。
        /// </summary>
        public static object GetMainCameraHandle()
        {
            EnsureInstalled();

            if (_cachedMain != null && SafeIsAlive(_cachedMain))
            {
                Interlocked.Increment(ref _cacheHits);
                return _cachedMain;
            }

            Interlocked.Increment(ref _resolveCount);

            object camera = null;

            // 1) Camera.main(Unity 认定的主相机; 未启用时 Unity 自己就返回 null)
            try { camera = _backend.GetMainCamera(); } catch { }
            if (!SafeIsAlive(camera)) camera = null;

            // 2) tag == MainCamera(启用中)
            if (camera == null) camera = FindByTag("MainCamera", false);

            // 3) 名字含 main(启用中)
            if (camera == null) camera = FindByNameContains("main", false);

            // 4/5) 兜底: 允许失活 —— 过渡期游戏只有这一台相机时仍能解析到它(旧行为)
            if (camera == null) camera = FindByTag("MainCamera", true);
            if (camera == null) camera = FindByNameContains("main", true);

            // 6) 最靠前的启用相机
            if (camera == null)
            {
                var all = SafeGetAll(false);
                if (all.Length > 0) camera = all[0];
            }

            _cachedMain = camera;
            return camera;
        }

        /// <summary>取主相机(强类型; 没有则 null)。</summary>
        public static Camera GetMainCamera()
        {
            return GetMainCameraHandle() as Camera;
        }

        /// <summary>SDK 当前认定的活动相机(未显式设置时即主相机)。</summary>
        public static object GetActiveCameraHandle()
        {
            if (_activeCamera != null && SafeIsAlive(_activeCamera)) return _activeCamera;
            return GetMainCameraHandle();
        }

        /// <summary>SDK 当前认定的活动相机(强类型)。</summary>
        public static Camera GetActiveCamera()
        {
            return GetActiveCameraHandle() as Camera;
        }

        /// <summary>
        /// 把某个相机记为 SDK 的活动相机并启用它。
        ///
        /// 注意: Unity 的 <c>Camera.current</c> 是只读的, 无法真正"设置"。
        /// 若相机由 Cinemachine 驱动, 还需要调 <see cref="CinemachineService.SetActiveVirtualCameraPriority"/>
        /// 才能真正接管画面 —— 本方法只做 SDK 侧登记 + 启用。
        /// </summary>
        public static bool SetActiveCamera(Camera camera)
        {
            if (!UnityObject.IsAlive(camera)) return false;
            _activeCamera = camera;
            try { camera.enabled = true; } catch { }
            SdkLog.Debug("CAMERA", "活动相机设为: " + UnityObject.GetHierarchyPath(camera));
            return true;
        }

        /// <summary>清空 SDK 侧的活动相机登记。</summary>
        public static void ClearActiveCamera()
        {
            _activeCamera = null;
        }

        /// <summary>全部相机句柄(按 depth 降序)。</summary>
        public static object[] GetAllCameraHandles(bool includeDisabled = true)
        {
            return SafeGetAll(includeDisabled);
        }

        /// <summary>全部相机(强类型)。</summary>
        public static Camera[] GetAllCameras(bool includeDisabled = true)
        {
            var handles = SafeGetAll(includeDisabled);
            var result = new List<Camera>(handles.Length);
            for (int i = 0; i < handles.Length; i++)
            {
                var cam = handles[i] as Camera;
                if (cam != null) result.Add(cam);
            }
            return result.ToArray();
        }

        /// <summary>相机是否仍然存在(未销毁)。</summary>
        public static bool IsCameraAlive(object camera)
        {
            return SafeIsAlive(camera);
        }

        /// <summary>相机是否仍然存在。</summary>
        public static bool IsCameraAlive(Camera camera)
        {
            return UnityObject.IsAlive(camera);
        }

        /// <summary>
        /// 按名字或层级路径查找相机(先精确名字, 再路径后缀, 最后包含匹配)。
        /// </summary>
        public static object FindCameraHandle(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return null;

            // 常见情况: 直接问主相机
            if (string.Equals(nameOrPath, "main camera", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nameOrPath, "main", StringComparison.OrdinalIgnoreCase))
            {
                var main = GetMainCameraHandle();
                if (main != null) return main;
            }

            var all = SafeGetAll(true);
            if (all.Length == 0) return null;

            // 精确名字
            for (int i = 0; i < all.Length; i++)
            {
                if (string.Equals(SafeName(all[i]), nameOrPath, StringComparison.OrdinalIgnoreCase)) return all[i];
            }

            // 传入的是层级路径(含 '/')时, 用最后一段做精确名字匹配:
            // 这样即使相机读不出层级路径(例如测试替身 / 刚创建的对象)也能命中。
            int slash = nameOrPath.LastIndexOf('/');
            if (slash >= 0 && slash < nameOrPath.Length - 1)
            {
                string leaf = nameOrPath.Substring(slash + 1);
                for (int i = 0; i < all.Length; i++)
                {
                    if (string.Equals(SafeName(all[i]), leaf, StringComparison.OrdinalIgnoreCase)) return all[i];
                }
            }

            // 层级路径后缀
            for (int i = 0; i < all.Length; i++)
            {
                string path = SafePath(all[i]);
                if (!string.IsNullOrEmpty(path) &&
                    path.EndsWith("/" + nameOrPath, StringComparison.OrdinalIgnoreCase)) return all[i];
            }

            // 包含匹配
            for (int i = 0; i < all.Length; i++)
            {
                string name = SafeName(all[i]);
                if (!string.IsNullOrEmpty(name) &&
                    name.IndexOf(nameOrPath, StringComparison.OrdinalIgnoreCase) >= 0) return all[i];
            }

            return null;
        }

        /// <summary>按名字查找相机(强类型)。</summary>
        public static Camera FindCamera(string nameOrPath)
        {
            return FindCameraHandle(nameOrPath) as Camera;
        }

        // =====================================================================
        // 创建 / 销毁
        // =====================================================================

        /// <summary>创建一个独立相机(不挂到 SDK 清理链)。</summary>
        public static Camera CreateCamera(string name = "CesiumCamera", Transform parent = null)
        {
            return _backend.CreateCamera(name, parent) as Camera;
        }

        /// <summary>
        /// 创建受 SDK 管理的独立相机: 调用方 mod 卸载时自动销毁, 避免留下垃圾相机。
        /// </summary>
        public static Camera CreateOwnedCamera(string name = "CesiumCamera", Transform parent = null,
            ModContext owner = null)
        {
            var camera = CreateCamera(name, parent);
            if (camera == null) return null;

            var resolvedOwner = owner ?? SafeCurrentContext();
            if (resolvedOwner != null)
            {
                var captured = camera;
                resolvedOwner.RegisterCleanup(() =>
                {
                    try
                    {
                        if (UnityObject.IsAlive(captured)) UnityObject.SafeDestroy(captured.gameObject);
                    }
                    catch { }
                });
            }
            return camera;
        }

        /// <summary>销毁相机。</summary>
        public static bool DestroyCamera(Camera camera)
        {
            if (!UnityObject.IsAlive(camera)) return false;
            if (ReferenceEquals(_activeCamera, camera)) _activeCamera = null;
            if (ReferenceEquals(_cachedMain, camera)) _cachedMain = null;
            return _backend.DestroyCamera(camera);
        }

        // =====================================================================
        // 变换 / 参数读写(空安全)
        // =====================================================================

        /// <summary>世界坐标。</summary>
        public static Vector3 GetPosition(Camera camera) { return _backend.GetPosition(camera); }

        /// <summary>设置世界坐标。</summary>
        public static bool SetPosition(Camera camera, Vector3 value) { return _backend.SetPosition(camera, value); }

        /// <summary>世界旋转。</summary>
        public static Quaternion GetRotation(Camera camera) { return _backend.GetRotation(camera); }

        /// <summary>设置世界旋转。</summary>
        public static bool SetRotation(Camera camera, Quaternion value) { return _backend.SetRotation(camera, value); }

        /// <summary>世界欧拉角。</summary>
        public static Vector3 GetEulerAngles(Camera camera)
        {
            return UnityCall.ToEulerAngles(_backend.GetRotation(camera));
        }

        /// <summary>设置世界欧拉角。</summary>
        public static bool SetEulerAngles(Camera camera, Vector3 euler)
        {
            return _backend.SetRotation(camera, UnityCall.Euler(euler));
        }

        /// <summary>前方向量。</summary>
        public static Vector3 GetForward(Camera camera) { return _backend.GetForward(camera); }

        /// <summary>右方向量。</summary>
        public static Vector3 GetRight(Camera camera) { return _backend.GetRight(camera); }

        /// <summary>上方向量。</summary>
        public static Vector3 GetUp(Camera camera) { return _backend.GetUp(camera); }

        /// <summary>视场角。</summary>
        public static float GetFieldOfView(Camera camera) { return _backend.GetFieldOfView(camera); }

        /// <summary>设置视场角。</summary>
        public static bool SetFieldOfView(Camera camera, float value) { return _backend.SetFieldOfView(camera, value); }

        /// <summary>近裁剪面。</summary>
        public static float GetNearClipPlane(Camera camera) { return _backend.GetNearClipPlane(camera); }

        /// <summary>设置近裁剪面。</summary>
        public static bool SetNearClipPlane(Camera camera, float value) { return _backend.SetNearClipPlane(camera, value); }

        /// <summary>远裁剪面。</summary>
        public static float GetFarClipPlane(Camera camera) { return _backend.GetFarClipPlane(camera); }

        /// <summary>设置远裁剪面。</summary>
        public static bool SetFarClipPlane(Camera camera, float value) { return _backend.SetFarClipPlane(camera, value); }

        /// <summary>是否正交。</summary>
        public static bool GetOrthographic(Camera camera) { return _backend.GetOrthographic(camera); }

        /// <summary>设置正交。</summary>
        public static bool SetOrthographic(Camera camera, bool value) { return _backend.SetOrthographic(camera, value); }

        /// <summary>正交尺寸。</summary>
        public static float GetOrthographicSize(Camera camera) { return _backend.GetOrthographicSize(camera); }

        /// <summary>设置正交尺寸。</summary>
        public static bool SetOrthographicSize(Camera camera, float value) { return _backend.SetOrthographicSize(camera, value); }

        /// <summary>是否启用。</summary>
        public static bool GetEnabled(Camera camera) { return _backend.GetEnabled(camera); }

        /// <summary>设置启用。</summary>
        public static bool SetEnabled(Camera camera, bool value) { return _backend.SetEnabled(camera, value); }

        /// <summary>render depth。</summary>
        public static float GetDepth(Camera camera) { return _backend.GetDepth(camera); }

        /// <summary>设置 depth。</summary>
        public static bool SetDepth(Camera camera, float value) { return _backend.SetDepth(camera, value); }

        /// <summary>剔除遮罩。</summary>
        public static int GetCullingMask(Camera camera) { return _backend.GetCullingMask(camera); }

        /// <summary>设置剔除遮罩。</summary>
        public static bool SetCullingMask(Camera camera, int value) { return _backend.SetCullingMask(camera, value); }

        /// <summary>清屏标志。</summary>
        public static int GetClearFlags(Camera camera) { return _backend.GetClearFlags(camera); }

        /// <summary>设置清屏标志。</summary>
        public static bool SetClearFlags(Camera camera, int value) { return _backend.SetClearFlags(camera, value); }

        /// <summary>背景色。</summary>
        public static Color GetBackgroundColor(Camera camera) { return _backend.GetBackgroundColor(camera); }

        /// <summary>设置背景色。</summary>
        public static bool SetBackgroundColor(Camera camera, Color value) { return _backend.SetBackgroundColor(camera, value); }

        /// <summary>宽高比。</summary>
        public static float GetAspect(Camera camera) { return _backend.GetAspect(camera); }

        /// <summary>设置宽高比。</summary>
        public static bool SetAspect(Camera camera, float value) { return _backend.SetAspect(camera, value); }

        /// <summary>相机指向的射线的原点/方向(用于拾取), 失败返回 false。</summary>
        public static bool TryGetCenterRay(Camera camera, out Vector3 origin, out Vector3 direction)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;
            if (!UnityObject.IsAlive(camera)) return false;
            try
            {
                origin = camera.transform.position;
                direction = camera.transform.forward;
                return true;
            }
            catch { return false; }
        }

        // =====================================================================
        // 状态快照
        // =====================================================================

        /// <summary>采集相机完整状态。</summary>
        public static CameraState CaptureState(Camera camera)
        {
            return CameraState.Capture(_backend, camera);
        }

        /// <summary>采集相机完整状态(句柄版)。</summary>
        public static CameraState CaptureState(object camera)
        {
            return CameraState.Capture(_backend, camera);
        }

        /// <summary>把状态写回相机(默认不还原 enabled, 见 <see cref="CameraState.Restore"/>)。</summary>
        public static bool RestoreState(Camera camera, CameraState state, bool restoreEnabled = false)
        {
            return CameraState.Restore(_backend, camera, state, restoreEnabled);
        }

        /// <summary>把状态写回相机(句柄版; 默认不还原 enabled)。</summary>
        public static bool RestoreState(object camera, CameraState state, bool restoreEnabled = false)
        {
            return CameraState.Restore(_backend, camera, state, restoreEnabled);
        }

        /// <summary>序列化相机状态为 JSON。</summary>
        public static string SerializeState(CameraState state)
        {
            return state.ToJson();
        }

        /// <summary>从 JSON 解析相机状态(失败返回 false, 不抛异常)。</summary>
        public static bool TryDeserializeState(string json, out CameraState state)
        {
            return CameraState.TryParse(json, out state);
        }

        /// <summary>把相机状态写到文件。</summary>
        public static bool SaveState(string path, CameraState state)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, state.ToJsonPretty());
                SdkLog.Debug("CAMERA", "相机状态已保存: " + path);
                return true;
            }
            catch (Exception e)
            {
                SdkLog.Error("CAMERA", "保存相机状态失败: " + e.Message);
                return false;
            }
        }

        /// <summary>从文件读相机状态。</summary>
        public static bool TryLoadState(string path, out CameraState state)
        {
            state = default(CameraState);
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (!File.Exists(path)) return false;
                return CameraState.TryParse(File.ReadAllText(path), out state);
            }
            catch (Exception e)
            {
                SdkLog.Error("CAMERA", "读取相机状态失败: " + e.Message);
                return false;
            }
        }

        // =====================================================================
        // 缓存 / 诊断
        // =====================================================================

        /// <summary>清空相机缓存(场景切换时 SDK 会自动调用)。</summary>
        public static void InvalidateCaches()
        {
            _cachedMain = null;
        }

        /// <summary>相机状态摘要文本(诊断用)。</summary>
        public static string DescribeCamera(Camera camera)
        {
            if (!UnityObject.IsAlive(camera)) return "(相机无效/已销毁)";
            try
            {
                return UnityObject.GetHierarchyPath(camera) +
                       " enabled=" + camera.enabled +
                       " depth=" + camera.depth +
                       " fov=" + camera.fieldOfView.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                       " ortho=" + camera.orthographic +
                       " pos=" + camera.transform.position;
            }
            catch (Exception e) { return "(读取失败: " + e.Message + ")"; }
        }

        // =====================================================================
        // 内部
        // =====================================================================

        private static void EnsureInstalled()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;

            try
            {
                SceneService.SubscribeActiveSceneChanged((previous, current) =>
                {
                    InvalidateCaches();
                    ClearActiveCamera();
                    SdkLog.Debug("CAMERA", "场景切换, 相机缓存已失效: " + SafeSceneName(current));
                }, null);
            }
            catch (Exception e)
            {
                SdkLog.Warn("CAMERA", "注册场景切换失效钩子失败: " + e.Message);
            }
        }

        private static object[] SafeGetAll(bool includeDisabled)
        {
            try { return _backend.GetAllCameras(includeDisabled) ?? new object[0]; }
            catch (Exception e)
            {
                SdkLog.Error("CAMERA", "枚举相机失败: " + e.Message);
                return new object[0];
            }
        }

        private static bool SafeIsAlive(object camera)
        {
            if (camera == null) return false;
            try { return _backend.IsAlive(camera); }
            catch { return false; }
        }

        private static string SafeName(object camera)
        {
            try { return _backend.GetName(camera); } catch { return null; }
        }

        private static string SafePath(object camera)
        {
            try { return _backend.GetHierarchyPath(camera); } catch { return null; }
        }

        private static string SafeTag(object camera)
        {
            try { return _backend.GetTag(camera); } catch { return null; }
        }

        /// <summary>按 tag 找相机。<paramref name="includeDisabled"/>=false 时只认启用中的相机
        /// (主相机解析优先用它, 避免在同时存在启用/失活相机时挑到过渡期那台)。</summary>
        private static object FindByTag(string tag, bool includeDisabled = true)
        {
            var all = SafeGetAll(includeDisabled);
            for (int i = 0; i < all.Length; i++)
            {
                if (!includeDisabled && !SafeEnabled(all[i])) continue;
                if (string.Equals(SafeTag(all[i]), tag, StringComparison.Ordinal)) return all[i];
            }
            return null;
        }

        private static object FindByNameContains(string fragment, bool includeDisabled = true)
        {
            var all = SafeGetAll(includeDisabled);
            for (int i = 0; i < all.Length; i++)
            {
                if (!includeDisabled && !SafeEnabled(all[i])) continue;
                string name = SafeName(all[i]);
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return all[i];
            }
            return null;
        }

        /// <summary>相机是否启用(读取失败视为启用, 与旧行为一致, 避免因读不到 enabled 而解析不出相机)。</summary>
        private static bool SafeEnabled(object camera)
        {
            if (camera == null) return false;
            try { return _backend.GetEnabled(camera); }
            catch { return true; }
        }

        private static string SafeSceneName(UnityEngine.SceneManagement.Scene scene)
        {
            try { return scene.IsValid() ? scene.name : "?"; } catch { return "?"; }
        }

        private static ModContext SafeCurrentContext()
        {
            try { return ModContext.Current; }
            catch { return null; }
        }
    }
}
