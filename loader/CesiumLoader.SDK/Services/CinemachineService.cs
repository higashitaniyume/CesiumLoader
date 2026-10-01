using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// Cinemachine 接管状态快照。用于"进入自由相机前保存 / 退出后完整还原"。
    /// </summary>
    [Serializable]
    public struct CinemachineBrainState
    {
        /// <summary>Cinemachine 是否可用。</summary>
        public bool Available;

        /// <summary>是否找到 Brain。</summary>
        public bool BrainFound;

        /// <summary>Brain 当时是否启用。</summary>
        public bool BrainEnabled;

        /// <summary>接管中的 VirtualCamera 名字(可能为空)。</summary>
        public string ActiveVirtualCameraName;

        /// <summary>当时是否正在混合。</summary>
        public bool IsBlending;

        /// <summary>我们创建的临时 VirtualCamera 名字(用于还原时销毁)。</summary>
        public string CreatedVirtualCameraName;

        /// <summary>摘要文本。</summary>
        public override string ToString()
        {
            if (!Available) return "Cinemachine(不可用)";
            if (!BrainFound) return "Cinemachine(未找到 Brain)";
            return "Cinemachine[brain enabled=" + BrainEnabled +
                   " active=" + (string.IsNullOrEmpty(ActiveVirtualCameraName) ? "(无)" : ActiveVirtualCameraName) +
                   " blending=" + IsBlending + "]";
        }
    }

    /// <summary>
    /// Cinemachine 适配层(<b>全反射实现</b>)。
    ///
    /// 为什么不直接引用 Cinemachine.dll:
    ///  1) 该程序集对 Unity.Timeline 有依赖, 编译期引用会把 Timeline 变成 SDK 的硬依赖;
    ///  2) SDK 需要在"游戏里没有 Cinemachine"时也能正常加载 —— 反射可以优雅降级,
    ///     直接引用则可能在 JIT 时抛 TypeLoadException。
    /// 所有成员只在首次访问时解析并缓存; 任何一步失败都返回 false/空, 绝不抛到 mod。
    ///
    /// 已确认本游戏使用 Cinemachine 2.x: 类型 Cinemachine.CinemachineBrain /
    /// Cinemachine.CinemachineVirtualCamera / Cinemachine.CinemachineCore 均存在。
    /// </summary>
    public static class CinemachineService
    {
        private static readonly object _lock = new object();
        private static bool _resolved;

        private static Type _brainType;
        private static Type _vcamBaseType;
        private static Type _vcamType;
        private static Type _coreType;
        private static Type _lensType;

        private static PropertyInfo _brainActiveVcam;
        private static PropertyInfo _brainIsBlending;
        private static PropertyInfo _vcamPriority;
        private static FieldInfo _vcamPriorityField;
        private static FieldInfo _vcamLensField;
        private static FieldInfo _lensFovField;
        private static FieldInfo _lensNearField;
        private static FieldInfo _lensFarField;
        private static PropertyInfo _vcamNameProperty;
        private static PropertyInfo _vcamFollowProperty;
        private static PropertyInfo _vcamLookAtProperty;
        private static PropertyInfo _behaviourEnabled;
        private static PropertyInfo _coreInstance;

        private static long _resolveAttempts;
        private static long _createCount;
        private static long _failCount;

        // =====================================================================
        // 能力探测
        // =====================================================================

        /// <summary>Cinemachine 类型是否可解析(程序集已加载)。</summary>
        public static bool IsAvailable
        {
            get { Resolve(); return _brainType != null && _vcamBaseType != null; }
        }

        /// <summary>Cinemachine 是否已初始化(CinemachineCore.Instance 非空)。</summary>
        public static bool IsInitialized
        {
            get
            {
                Resolve();
                if (_coreType == null) return false;
                try
                {
                    if (_coreInstance == null) return false;
                    return _coreInstance.GetValue(null, null) != null;
                }
                catch { return false; }
            }
        }

        /// <summary>解析尝试次数。</summary>
        public static long ResolveAttemptCount { get { return Interlocked.Read(ref _resolveAttempts); } }

        /// <summary>创建过的 VirtualCamera 数量。</summary>
        public static long CreatedVirtualCameraCount { get { return Interlocked.Read(ref _createCount); } }

        /// <summary>失败次数。</summary>
        public static long FailureCount { get { return Interlocked.Read(ref _failCount); } }

        /// <summary>Brain 类型(可能为 null)。</summary>
        public static Type BrainType { get { Resolve(); return _brainType; } }

        /// <summary>VirtualCamera 基类类型(可能为 null)。</summary>
        public static Type VirtualCameraBaseType { get { Resolve(); return _vcamBaseType; } }

        // =====================================================================
        // Brain
        // =====================================================================

        /// <summary>查找场景中的第一个 CinemachineBrain(找不到返回 null)。</summary>
        public static object FindBrain()
        {
            Resolve();
            if (_brainType == null) return null;

            var all = FindComponents(_brainType, true);
            for (int i = 0; i < all.Count; i++)
            {
                if (UnityObject.IsAlive(all[i] as UnityEngine.Object)) return all[i];
            }
            return null;
        }

        /// <summary>查找全部 CinemachineBrain。</summary>
        public static List<object> FindBrains()
        {
            Resolve();
            if (_brainType == null) return new List<object>();

            var found = FindComponents(_brainType, true);
            var result = new List<object>(found.Count);
            for (int i = 0; i < found.Count; i++)
            {
                if (UnityObject.IsAlive(found[i] as UnityEngine.Object)) result.Add(found[i]);
            }
            return result;
        }

        /// <summary>brain 当前接管的 VirtualCamera(找不到返回 null)。</summary>
        public static object GetActiveVirtualCamera(object brain)
        {
            if (brain == null) return null;
            Resolve();
            if (_brainActiveVcam == null) return null;

            try
            {
                object value = _brainActiveVcam.GetValue(brain, null);
                return value as UnityEngine.Object != null || value != null ? value : null;
            }
            catch { Interlocked.Increment(ref _failCount); return null; }
        }

        /// <summary>brain 当前接管相机的名字。</summary>
        public static string GetActiveVirtualCameraName(object brain)
        {
            var vcam = GetActiveVirtualCamera(brain);
            return vcam != null ? GetVirtualCameraName(vcam) : null;
        }

        /// <summary>brain 是否正在混合。</summary>
        public static bool IsBlending(object brain)
        {
            if (brain == null) return false;
            Resolve();
            if (_brainIsBlending == null) return false;
            try { return (bool)_brainIsBlending.GetValue(brain, null); }
            catch { return false; }
        }

        /// <summary>启用/禁用 Brian(禁用后 Unity 相机不再被每帧覆盖)。</summary>
        public static bool SetBrainEnabled(object brain, bool enabled)
        {
            if (brain == null) return false;
            Resolve();
            try
            {
                var prop = _behaviourEnabled ?? (_behaviourEnabled =
                    RuntimeAssemblyService.FindProperty(brain.GetType(), "enabled"));
                if (prop == null || !prop.CanWrite) return false;
                prop.SetValue(brain, enabled, null);
                return true;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failCount);
                SdkLog.Warn("CINEMACHINE", "设置 Brain.enabled 失败: " + e.Message);
                return false;
            }
        }

        /// <summary>brain 是否启用。</summary>
        public static bool IsBrainEnabled(object brain)
        {
            if (brain == null) return false;
            try
            {
                var prop = RuntimeAssemblyService.FindProperty(brain.GetType(), "enabled");
                return prop != null && prop.CanRead && (bool)prop.GetValue(brain, null);
            }
            catch { return false; }
        }

        // =====================================================================
        // VirtualCamera
        // =====================================================================

        /// <summary>查找全部 VirtualCamera(含未激活)。</summary>
        public static List<object> GetVirtualCameras(bool includeInactive = true)
        {
            Resolve();
            if (_vcamBaseType == null) return new List<object>();

            var found = FindComponents(_vcamBaseType, includeInactive);
            var result = new List<object>(found.Count);
            for (int i = 0; i < found.Count; i++)
            {
                if (UnityObject.IsAlive(found[i] as UnityEngine.Object)) result.Add(found[i]);
            }
            return result;
        }

        /// <summary>按名字查找 VirtualCamera。</summary>
        public static object FindVirtualCameraByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var all = GetVirtualCameras(true);
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(GetVirtualCameraName(all[i]), name, StringComparison.OrdinalIgnoreCase))
                    return all[i];
            }
            return null;
        }

        /// <summary>VirtualCamera 名字。</summary>
        public static string GetVirtualCameraName(object vcam)
        {
            if (vcam == null) return null;
            Resolve();
            try
            {
                if (_vcamNameProperty != null && _vcamNameProperty.CanRead)
                {
                    object value = _vcamNameProperty.GetValue(vcam, null);
                    if (value is string s && !string.IsNullOrEmpty(s)) return s;
                }
                return UnityObject.GetName(vcam as UnityEngine.Object);
            }
            catch { return null; }
        }

        /// <summary>读取优先级。</summary>
        public static int GetPriority(object vcam)
        {
            if (vcam == null) return 0;
            Resolve();
            try
            {
                if (_vcamPriority != null && _vcamPriority.CanRead)
                    return Convert.ToInt32(_vcamPriority.GetValue(vcam, null));
                if (_vcamPriorityField != null)
                    return Convert.ToInt32(_vcamPriorityField.GetValue(vcam));
            }
            catch { Interlocked.Increment(ref _failCount); }
            return 0;
        }

        /// <summary>设置优先级(提升优先级即可让 brain 切换到该相机)。</summary>
        public static bool SetPriority(object vcam, int priority)
        {
            if (vcam == null) return false;
            Resolve();
            try
            {
                if (_vcamPriority != null && _vcamPriority.CanWrite)
                {
                    _vcamPriority.SetValue(vcam, priority, null);
                    return true;
                }
                if (_vcamPriorityField != null)
                {
                    _vcamPriorityField.SetValue(vcam, priority);
                    return true;
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failCount);
                SdkLog.Warn("CINEMACHINE", "设置 VirtualCamera 优先级失败: " + e.Message);
            }
            return false;
        }

        /// <summary>设置 Follow 目标。</summary>
        public static bool SetFollow(object vcam, Transform target)
        {
            if (vcam == null) return false;
            Resolve();
            try
            {
                if (_vcamFollowProperty == null || !_vcamFollowProperty.CanWrite) return false;
                _vcamFollowProperty.SetValue(vcam, target, null);
                return true;
            }
            catch { return false; }
        }

        /// <summary>设置 LookAt 目标。</summary>
        public static bool SetLookAt(object vcam, Transform target)
        {
            if (vcam == null) return false;
            Resolve();
            try
            {
                if (_vcamLookAtProperty == null || !_vcamLookAtProperty.CanWrite) return false;
                _vcamLookAtProperty.SetValue(vcam, target, null);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 创建一个独立的 VirtualCamera 并挂到指定父节点下。
        /// 失败返回 null(Cinemachine 不可用时属正常情况)。
        /// </summary>
        public static object CreateVirtualCamera(string name = "CesiumFreeCamera", Transform parent = null,
            int priority = 0, bool disableFollowTargets = true)
        {
            Resolve();
            if (_vcamType == null)
            {
                SdkLog.Debug("CINEMACHINE", "Cinemachine 不可用, 无法创建 VirtualCamera");
                return null;
            }

            try
            {
                var go = GameObjectUtil.Create(string.IsNullOrEmpty(name) ? "CesiumFreeCamera" : name, parent);
                if (go == null) return null;

                var vcam = GameObjectUtil.AddComponent(go, _vcamType);
                if (vcam == null)
                {
                    UnityObject.SafeDestroy(go);
                    return null;
                }

                // 自由相机不应被 Follow/LookAt 拉走
                if (disableFollowTargets)
                {
                    SetFollow(vcam, null);
                    SetLookAt(vcam, null);
                }
                SetPriority(vcam, priority);

                Interlocked.Increment(ref _createCount);
                SdkLog.Info("CINEMACHINE", "已创建 VirtualCamera: " + go.name + " (priority=" + priority + ")");
                return vcam;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failCount);
                SdkLog.Error("CINEMACHINE", "创建 VirtualCamera 失败: " + e.Message);
                return null;
            }
        }

        /// <summary>销毁一个 VirtualCamera(连同它的 GameObject)。</summary>
        public static bool DestroyVirtualCamera(object vcam)
        {
            if (vcam == null) return false;
            try
            {
                var component = vcam as Component;
                if (component != null) return UnityObject.SafeDestroy(component.gameObject);
                var obj = vcam as UnityEngine.Object;
                return obj != null && UnityObject.SafeDestroy(obj);
            }
            catch { return false; }
        }

        // =====================================================================
        // Lens(FOV / 裁剪面)
        // =====================================================================

        /// <summary>读取 VirtualCamera 的镜头 FOV(失败返回 0)。</summary>
        public static float GetLensFieldOfView(object vcam)
        {
            return GetLensFloat(vcam, _lensFovField);
        }

        /// <summary>设置 VirtualCamera 的镜头 FOV。</summary>
        public static bool SetLensFieldOfView(object vcam, float value)
        {
            return SetLensFloat(vcam, _lensFovField, value);
        }

        /// <summary>设置 VirtualCamera 的近裁剪面。</summary>
        public static bool SetLensNearClipPlane(object vcam, float value)
        {
            return SetLensFloat(vcam, _lensNearField, value);
        }

        /// <summary>设置 VirtualCamera 的远裁剪面。</summary>
        public static bool SetLensFarClipPlane(object vcam, float value)
        {
            return SetLensFloat(vcam, _lensFarField, value);
        }

        /// <summary>读取 VirtualCamera 的镜头正交开关。</summary>
        public static bool SetLensOrthographic(object vcam, bool value)
        {
            if (vcam == null || _lensType == null || _vcamLensField == null) return false;
            try
            {
                object lens = _vcamLensField.GetValue(vcam);
                if (lens == null) return false;
                var field = RuntimeAssemblyService.FindField(_lensType, "Orthographic");
                if (field == null) return false;
                field.SetValue(lens, value);
                _vcamLensField.SetValue(vcam, lens);
                return true;
            }
            catch { return false; }
        }

        /// <summary>读取 VirtualCamera 的镜头正交尺寸。</summary>
        public static float GetLensOrthographicSize(object vcam)
        {
            return GetLensFloat(vcam, RuntimeAssemblyService.FindField(_lensType, "OrthographicSize"));
        }

        /// <summary>设置 VirtualCamera 的镜头正交尺寸。</summary>
        public static bool SetLensOrthographicSize(object vcam, float value)
        {
            return SetLensFloat(vcam, RuntimeAssemblyService.FindField(_lensType, "OrthographicSize"), value);
        }

        private static float GetLensFloat(object vcam, FieldInfo lensField)
        {
            if (vcam == null || lensField == null || _vcamLensField == null) return 0f;
            try
            {
                object lens = _vcamLensField.GetValue(vcam);
                if (lens == null) return 0f;
                object value = lensField.GetValue(lens);
                return value != null ? Convert.ToSingle(value) : 0f;
            }
            catch { return 0f; }
        }

        private static bool SetLensFloat(object vcam, FieldInfo lensField, float value)
        {
            if (vcam == null || lensField == null || _vcamLensField == null) return false;
            try
            {
                object lens = _vcamLensField.GetValue(vcam);
                if (lens == null) return false;

                // LensSettings 是结构体: 必须在装箱对象上改, 再写回字段
                lensField.SetValue(lens, value);
                _vcamLensField.SetValue(vcam, lens);
                return true;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failCount);
                SdkLog.Debug("CINEMACHINE", "设置镜头参数失败: " + e.Message);
                return false;
            }
        }

        // =====================================================================
        // 状态保存 / 还原
        // =====================================================================

        /// <summary>采集 Cinemachine 接管状态。</summary>
        public static CinemachineBrainState CaptureBrainState()
        {
            var state = new CinemachineBrainState { Available = IsAvailable };
            if (!state.Available) return state;

            try
            {
                var brain = FindBrain();
                if (brain == null) return state;

                state.BrainFound = true;
                state.BrainEnabled = IsBrainEnabled(brain);
                state.ActiveVirtualCameraName = GetActiveVirtualCameraName(brain);
                state.IsBlending = IsBlending(brain);
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash("CINEMACHINE", "采集 Brain 状态", e);
            }
            return state;
        }

        /// <summary>
        /// 还原 Cinemachine 接管状态: 恢复 Brain 的启用状态。
        /// 若 <paramref name="createdVirtualCamera"/> 非空则销毁它。
        /// </summary>
        public static bool RestoreBrainState(CinemachineBrainState state, object createdVirtualCamera = null)
        {
            bool ok = true;
            try
            {
                if (createdVirtualCamera != null) DestroyVirtualCamera(createdVirtualCamera);

                if (state.Available && state.BrainFound)
                {
                    var brain = FindBrain();
                    if (brain != null) ok &= SetBrainEnabled(brain, state.BrainEnabled);
                }
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash("CINEMACHINE", "还原 Brain 状态", e);
                return false;
            }
            return ok;
        }

        // =====================================================================
        // 诊断
        // =====================================================================

        /// <summary>文本状态(诊断输出用)。</summary>
        public static string Describe()
        {
            try
            {
                if (!IsAvailable) return "Cinemachine: 不可用(类型未解析到)";

                var lines = new List<string>();
                lines.Add("Cinemachine: 可用, initialized=" + IsInitialized);

                var brains = FindBrains();
                lines.Add("  Brain 数量: " + brains.Count);
                for (int i = 0; i < brains.Count; i++)
                {
                    lines.Add("  - " + UnityObject.GetHierarchyPath(brains[i] as UnityEngine.Object) +
                              " enabled=" + IsBrainEnabled(brains[i]) +
                              " blending=" + IsBlending(brains[i]) +
                              " active=" + (GetActiveVirtualCameraName(brains[i]) ?? "(无)"));
                }

                var vcams = GetVirtualCameras(true);
                lines.Add("  VirtualCamera 数量: " + vcams.Count);
                for (int i = 0; i < vcams.Count && i < 24; i++)
                {
                    lines.Add("  - " + GetVirtualCameraName(vcams[i]) +
                              " priority=" + GetPriority(vcams[i]) +
                              " fov=" + GetLensFieldOfView(vcams[i]).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (vcams.Count > 24) lines.Add("  ... (还有 " + (vcams.Count - 24) + " 个)");

                return string.Join("\n", lines.ToArray());
            }
            catch (Exception e)
            {
                return "Cinemachine 诊断失败: " + e.Message;
            }
        }

        // =====================================================================
        // 内部
        // =====================================================================

        private static void Resolve()
        {
            if (Volatile.Read(ref _resolved)) return;

            lock (_lock)
            {
                if (_resolved) return;
                Interlocked.Increment(ref _resolveAttempts);

                _brainType = RuntimeAssemblyService.FindType("Cinemachine.CinemachineBrain");
                _vcamBaseType = RuntimeAssemblyService.FindType("Cinemachine.CinemachineVirtualCameraBase");
                _vcamType = RuntimeAssemblyService.FindType("Cinemachine.CinemachineVirtualCamera");
                _coreType = RuntimeAssemblyService.FindType("Cinemachine.CinemachineCore");

                if (_vcamBaseType != null)
                {
                    _vcamPriority = RuntimeAssemblyService.FindProperty(_vcamBaseType, "Priority");
                    if (_vcamPriority == null || !_vcamPriority.CanWrite)
                        _vcamPriorityField = RuntimeAssemblyService.FindField(_vcamBaseType, "m_Priority");
                    _vcamNameProperty = RuntimeAssemblyService.FindProperty(_vcamBaseType, "Name");
                    _vcamFollowProperty = RuntimeAssemblyService.FindProperty(_vcamBaseType, "Follow");
                    _vcamLookAtProperty = RuntimeAssemblyService.FindProperty(_vcamBaseType, "LookAt");
                }

                if (_vcamType != null)
                {
                    _vcamLensField = RuntimeAssemblyService.FindField(_vcamType, "m_Lens");
                    if (_vcamLensField != null)
                    {
                        _lensType = _vcamLensField.FieldType;
                        _lensFovField = RuntimeAssemblyService.FindField(_lensType, "FieldOfView");
                        _lensNearField = RuntimeAssemblyService.FindField(_lensType, "NearClipPlane");
                        _lensFarField = RuntimeAssemblyService.FindField(_lensType, "FarClipPlane");
                    }
                }

                if (_brainType != null)
                {
                    _brainActiveVcam = RuntimeAssemblyService.FindProperty(_brainType, "ActiveVirtualCamera");
                    _brainIsBlending = RuntimeAssemblyService.FindProperty(_brainType, "IsBlending");
                }

                if (_coreType != null)
                    _coreInstance = RuntimeAssemblyService.FindProperty(_coreType, "Instance");

                Volatile.Write(ref _resolved, true);

                SdkLog.Debug("CINEMACHINE", "类型解析: brain=" + (_brainType != null) +
                                              " vcam=" + (_vcamType != null) +
                                              " lens=" + (_lensType != null));
            }
        }

        /// <summary>按运行时类型查找场景组件(避开泛型约束)。</summary>
        private static List<object> FindComponents(Type type, bool includeInactive)
        {
            var result = new List<object>();
            if (type == null) return result;

            MainThread.AssertMainThread("CinemachineService.FindComponents");
            try
            {
                var all = UnityCall.FindObjectsOfTypeAll(type);
                if (all == null) return result;

                for (int i = 0; i < all.Length; i++)
                {
                    var obj = all[i] as Component;
                    if (obj == null) continue;
                    if (!UnityObject.IsSceneObject(obj)) continue;
                    if (!includeInactive && !UnityObject.IsActiveInHierarchy(obj)) continue;
                    result.Add(obj);
                }
            }
            catch (Exception e)
            {
                SdkLog.Error("CINEMACHINE", "查找组件失败 " + type.Name + ": " + e.Message);
            }
            return result;
        }
    }
}
