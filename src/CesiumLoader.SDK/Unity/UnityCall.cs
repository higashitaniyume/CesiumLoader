using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// Unity ECall 隔离层 —— <b>这是整个 SDK 里唯一允许直接触碰 Unity ECall 的地方</b>。
    ///
    /// 为什么必须有这一层(实测结论, 不是理论):
    ///   Unity 的 ECall 成员(Time.*、Cursor.*、Camera.main、Transform.position、
    ///   Camera.fieldOfView、GameObject.Find、Debug.Log ……)在 IL2CPP 里是 InternalCall,
    ///   而 InternalCall 的失败发生在<b>方法 JIT 时</b>, 它会直接绕过该方法的 try/catch。
    ///   也就是说:
    ///
    ///       // ✗ 无效防御 —— 异常照样逃出去
    ///       float dt;
    ///       try { dt = Time.deltaTime; } catch { dt = 0f; }
    ///
    ///       // ✓ 有效防御 —— ECall 在别的方法里, JIT 失败在调用处被捕获
    ///       static float Raw() { return Time.deltaTime; }
    ///       float dt;
    ///       try { dt = Raw(); } catch { dt = 0f; }
    ///
    ///   本文件用 <c>Raw*</c> / 安全方法两层来实现后者:
    ///     - <c>RawXxx</c>: 方法体里<b>只有一个</b> Unity ECall, 绝不能含 try/catch(也没意义);
    ///     - <c>Xxx</c>(安全方法): 方法体里<b>只有</b> try/catch + 默认值, 绝不直接碰 Unity。
    ///   新增 Unity 调用时请沿用这个约定, 否则等于没有防御。
    ///
    /// 另一条同样重要的规则: <b>Raw 方法不要内联</b>。用 NoInlining 保证 JIT 边界真实存在。
    /// </summary>
    internal static class UnityCall
    {
        // =====================================================================
        // Time
        // =====================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawDeltaTime() { return Time.deltaTime; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawUnscaledDeltaTime() { return Time.unscaledDeltaTime; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawFixedDeltaTime() { return Time.fixedDeltaTime; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawTime() { return Time.time; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawRealtimeSinceStartup() { return Time.realtimeSinceStartup; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawFrameCount() { return Time.frameCount; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawTimeScale() { return Time.timeScale; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetTimeScale(float value) { Time.timeScale = value; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawSmoothDeltaTime() { return Time.smoothDeltaTime; }

        /// <summary>帧间隔秒; 不可用时 0。</summary>
        internal static float DeltaTime() { try { return RawDeltaTime(); } catch { return 0f; } }

        /// <summary>不受 timeScale 影响的帧间隔; 不可用时 0。</summary>
        internal static float UnscaledDeltaTime() { try { return RawUnscaledDeltaTime(); } catch { return 0f; } }

        /// <summary>固定帧间隔; 不可用时 0。</summary>
        internal static float FixedDeltaTime() { try { return RawFixedDeltaTime(); } catch { return 0f; } }

        /// <summary>游戏时间; 不可用时 0。</summary>
        internal static float GameTime() { try { return RawTime(); } catch { return 0f; } }

        /// <summary>真实时间(秒); 不可用时退回进程启动以来的秒数(仍然单调, 可用于计时)。</summary>
        internal static float RealtimeSinceStartup()
        {
            try { return RawRealtimeSinceStartup(); }
            catch { return Environment.TickCount / 1000f; }
        }

        /// <summary>帧号; 不可用时 0。</summary>
        internal static int FrameCount() { try { return RawFrameCount(); } catch { return 0; } }

        /// <summary>时间缩放; 不可用时 1。</summary>
        internal static float TimeScale() { try { return RawTimeScale(); } catch { return 1f; } }

        /// <summary>设置时间缩放; 不可用时忽略。</summary>
        internal static bool SetTimeScale(float value)
        {
            try { RawSetTimeScale(value); return true; }
            catch { return false; }
        }

        /// <summary>平滑帧间隔; 不可用时 0。</summary>
        internal static float SmoothDeltaTime() { try { return RawSmoothDeltaTime(); } catch { return 0f; } }

        // =====================================================================
        // Cursor
        // =====================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawCursorVisible() { return Cursor.visible; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetCursorVisible(bool value) { Cursor.visible = value; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static CursorLockMode RawCursorLockState() { return Cursor.lockState; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetCursorLockState(CursorLockMode mode) { Cursor.lockState = mode; }

        /// <summary>读光标可见性; 不可用时返回 false。</summary>
        internal static bool CursorVisible() { try { return RawCursorVisible(); } catch { return false; } }

        /// <summary>设置光标可见性; 不可用时返回 false。</summary>
        internal static bool SetCursorVisible(bool value)
        {
            try { RawSetCursorVisible(value); return true; }
            catch { return false; }
        }

        /// <summary>读光标锁定状态; 不可用时返回 None。</summary>
        internal static CursorLockMode CursorLockState()
        {
            try { return RawCursorLockState(); }
            catch { return CursorLockMode.None; }
        }

        /// <summary>设置光标锁定状态; 不可用时返回 false。</summary>
        internal static bool SetCursorLockState(CursorLockMode mode)
        {
            try { RawSetCursorLockState(mode); return true; }
            catch { return false; }
        }

        // =====================================================================
        // Screen
        // =====================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawScreenWidth() { return Screen.width; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawScreenHeight() { return Screen.height; }

        /// <summary>屏幕宽; 不可用时 0。</summary>
        internal static int ScreenWidth() { try { return RawScreenWidth(); } catch { return 0; } }

        /// <summary>屏幕高; 不可用时 0。</summary>
        internal static int ScreenHeight() { try { return RawScreenHeight(); } catch { return 0; } }

        // =====================================================================
        // Camera(只放"取相机实例"这类 ECall; 属性读写见下)
        // =====================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Camera RawMainCamera() { return Camera.main; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Camera[] RawAllCameras() { return Camera.allCameras; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawAllCamerasCount() { return Camera.allCamerasCount; }

        /// <summary>主相机(Camera.main); 不可用/不存在时 null。</summary>
        internal static Camera MainCamera() { try { return RawMainCamera(); } catch { return null; } }

        /// <summary>场景内所有相机; 不可用时空数组。</summary>
        internal static Camera[] AllCameras()
        {
            try { return RawAllCameras() ?? new Camera[0]; }
            catch { return new Camera[0]; }
        }

        /// <summary>相机数量; 不可用时 -1。</summary>
        internal static int AllCamerasCount() { try { return RawAllCamerasCount(); } catch { return -1; } }

        // 相机镜头属性(每个单独隔离, 否则 CameraBackend 里的 try/catch 无效)

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawFieldOfView(Camera c) { return c.fieldOfView; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetFieldOfView(Camera c, float v) { c.fieldOfView = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawNearClipPlane(Camera c) { return c.nearClipPlane; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetNearClipPlane(Camera c, float v) { c.nearClipPlane = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawFarClipPlane(Camera c) { return c.farClipPlane; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetFarClipPlane(Camera c, float v) { c.farClipPlane = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawCameraDepth(Camera c) { return c.depth; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawCameraOrthographic(Camera c) { return c.orthographic; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawCameraEnabled(Camera c) { return c.enabled; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawGameObjectActiveInHierarchy(Camera c) { return c.gameObject.activeInHierarchy; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RawCameraTag(Camera c) { return c.tag; }

        /// <summary>视野角; 不可用时 0。</summary>
        internal static float FieldOfView(Camera c)
        {
            if (c == null) return 0f;
            try { return RawFieldOfView(c); } catch { return 0f; }
        }

        /// <summary>设置视野角。</summary>
        internal static bool SetFieldOfView(Camera c, float v)
        {
            if (c == null) return false;
            try { RawSetFieldOfView(c, v); return true; } catch { return false; }
        }

        /// <summary>近裁剪面; 不可用时 0。</summary>
        internal static float NearClipPlane(Camera c)
        {
            if (c == null) return 0f;
            try { return RawNearClipPlane(c); } catch { return 0f; }
        }

        /// <summary>设置近裁剪面。</summary>
        internal static bool SetNearClipPlane(Camera c, float v)
        {
            if (c == null) return false;
            try { RawSetNearClipPlane(c, v); return true; } catch { return false; }
        }

        /// <summary>远裁剪面; 不可用时 0。</summary>
        internal static float FarClipPlane(Camera c)
        {
            if (c == null) return 0f;
            try { return RawFarClipPlane(c); } catch { return 0f; }
        }

        /// <summary>设置远裁剪面。</summary>
        internal static bool SetFarClipPlane(Camera c, float v)
        {
            if (c == null) return false;
            try { RawSetFarClipPlane(c, v); return true; } catch { return false; }
        }

        /// <summary>相机深度; 不可用时 0。</summary>
        internal static float CameraDepth(Camera c)
        {
            if (c == null) return 0f;
            try { return RawCameraDepth(c); } catch { return 0f; }
        }

        /// <summary>是否正交; 不可用时 false。</summary>
        internal static bool CameraOrthographic(Camera c)
        {
            if (c == null) return false;
            try { return RawCameraOrthographic(c); } catch { return false; }
        }

        /// <summary>组件是否启用; 不可用时 false。</summary>
        internal static bool CameraEnabled(Camera c)
        {
            if (c == null) return false;
            try { return RawCameraEnabled(c); } catch { return false; }
        }

        /// <summary>所在对象在层级里是否激活; 不可用时 false。</summary>
        internal static bool ActiveInHierarchy(Camera c)
        {
            if (c == null) return false;
            try { return RawGameObjectActiveInHierarchy(c); } catch { return false; }
        }

        /// <summary>Tag; 不可用时 null。</summary>
        internal static string CameraTag(Camera c)
        {
            if (c == null) return null;
            try { return RawCameraTag(c); } catch { return null; }
        }

        // =====================================================================
        // Transform
        // =====================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawPosition(Transform t) { return t.position; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetPosition(Transform t, Vector3 v) { t.position = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Quaternion RawRotation(Transform t) { return t.rotation; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetRotation(Transform t, Quaternion q) { t.rotation = q; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawEulerAngles(Transform t) { return t.eulerAngles; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetEulerAngles(Transform t, Vector3 v) { t.eulerAngles = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawLocalPosition(Transform t) { return t.localPosition; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetLocalPosition(Transform t, Vector3 v) { t.localPosition = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Quaternion RawLocalRotation(Transform t) { return t.localRotation; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetLocalRotation(Transform t, Quaternion q) { t.localRotation = q; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawLocalEulerAngles(Transform t) { return t.localEulerAngles; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetLocalEulerAngles(Transform t, Vector3 v) { t.localEulerAngles = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawLocalScale(Transform t) { return t.localScale; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetLocalScale(Transform t, Vector3 v) { t.localScale = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawForward(Transform t) { return t.forward; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawRight(Transform t) { return t.right; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawUp(Transform t) { return t.up; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Transform RawParent(Transform t) { return t.parent; }

        /// <summary>世界坐标; 不可用时 (0,0,0)。</summary>
        internal static Vector3 Position(Transform t)
        {
            if (t == null) return Vector3.zero;
            try { return RawPosition(t); } catch { return Vector3.zero; }
        }

        /// <summary>设置世界坐标。</summary>
        internal static bool SetPosition(Transform t, Vector3 v)
        {
            if (t == null) return false;
            try { RawSetPosition(t, v); return true; } catch { return false; }
        }

        /// <summary>世界旋转; 不可用时 identity。</summary>
        internal static Quaternion Rotation(Transform t)
        {
            if (t == null) return Quaternion.identity;
            try { return RawRotation(t); } catch { return Quaternion.identity; }
        }

        /// <summary>设置世界旋转。</summary>
        internal static bool SetRotation(Transform t, Quaternion q)
        {
            if (t == null) return false;
            try { RawSetRotation(t, q); return true; } catch { return false; }
        }

        /// <summary>欧拉角; 不可用时 (0,0,0)。</summary>
        internal static Vector3 EulerAngles(Transform t)
        {
            if (t == null) return Vector3.zero;
            try { return RawEulerAngles(t); } catch { return Vector3.zero; }
        }

        /// <summary>设置欧拉角。</summary>
        internal static bool SetEulerAngles(Transform t, Vector3 v)
        {
            if (t == null) return false;
            try { RawSetEulerAngles(t, v); return true; } catch { return false; }
        }

        /// <summary>局部坐标; 不可用时 (0,0,0)。</summary>
        internal static Vector3 LocalPosition(Transform t)
        {
            if (t == null) return Vector3.zero;
            try { return RawLocalPosition(t); } catch { return Vector3.zero; }
        }

        /// <summary>设置局部坐标。</summary>
        internal static bool SetLocalPosition(Transform t, Vector3 v)
        {
            if (t == null) return false;
            try { RawSetLocalPosition(t, v); return true; } catch { return false; }
        }

        /// <summary>局部旋转; 不可用时 identity。</summary>
        internal static Quaternion LocalRotation(Transform t)
        {
            if (t == null) return Quaternion.identity;
            try { return RawLocalRotation(t); } catch { return Quaternion.identity; }
        }

        /// <summary>设置局部旋转。</summary>
        internal static bool SetLocalRotation(Transform t, Quaternion q)
        {
            if (t == null) return false;
            try { RawSetLocalRotation(t, q); return true; } catch { return false; }
        }

        /// <summary>局部欧拉角; 不可用时 (0,0,0)。</summary>
        internal static Vector3 LocalEulerAngles(Transform t)
        {
            if (t == null) return Vector3.zero;
            try { return RawLocalEulerAngles(t); } catch { return Vector3.zero; }
        }

        /// <summary>设置局部欧拉角。</summary>
        internal static bool SetLocalEulerAngles(Transform t, Vector3 v)
        {
            if (t == null) return false;
            try { RawSetLocalEulerAngles(t, v); return true; } catch { return false; }
        }

        /// <summary>局部缩放; 不可用时 (1,1,1)。</summary>
        internal static Vector3 LocalScale(Transform t)
        {
            if (t == null) return Vector3.one;
            try { return RawLocalScale(t); } catch { return Vector3.one; }
        }

        /// <summary>设置局部缩放。</summary>
        internal static bool SetLocalScale(Transform t, Vector3 v)
        {
            if (t == null) return false;
            try { RawSetLocalScale(t, v); return true; } catch { return false; }
        }

        /// <summary>前方向; 不可用时 (0,0,1)。</summary>
        internal static Vector3 Forward(Transform t)
        {
            if (t == null) return Vector3.forward;
            try { return RawForward(t); } catch { return Vector3.forward; }
        }

        /// <summary>右方向; 不可用时 (1,0,0)。</summary>
        internal static Vector3 Right(Transform t)
        {
            if (t == null) return Vector3.right;
            try { return RawRight(t); } catch { return Vector3.right; }
        }

        /// <summary>上方向; 不可用时 (0,1,0)。</summary>
        internal static Vector3 Up(Transform t)
        {
            if (t == null) return Vector3.up;
            try { return RawUp(t); } catch { return Vector3.up; }
        }

        /// <summary>父节点; 不可用时 null。</summary>
        internal static Transform Parent(Transform t)
        {
            if (t == null) return null;
            try { return RawParent(t); } catch { return null; }
        }

        // Transform 的方法型成员(同样是 ECall, 也必须逐个隔离)

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawTranslate(Transform t, Vector3 v, Space space) { t.Translate(v, space); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawRotateEuler(Transform t, Vector3 euler, Space space) { t.Rotate(euler, space); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawRotateAxis(Transform t, Vector3 axis, float angle, Space space) { t.Rotate(axis, angle, space); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawLookAtPosition(Transform t, Vector3 worldPosition, Vector3 worldUp) { t.LookAt(worldPosition, worldUp); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawLookAtTransform(Transform t, Transform target) { t.LookAt(target); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetParent(Transform t, Transform parent, bool worldPositionStays) { t.SetParent(parent, worldPositionStays); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawChildCount(Transform t) { return t.childCount; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Transform RawGetChild(Transform t, int index) { return t.GetChild(index); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Transform RawFindChild(Transform t, string name) { return t.Find(name); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawInverseTransformPoint(Transform t, Vector3 position) { return t.InverseTransformPoint(position); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawTransformPoint(Transform t, Vector3 position) { return t.TransformPoint(position); }

        /// <summary>平移。</summary>
        internal static bool Translate(Transform t, Vector3 v, Space space)
        {
            if (t == null) return false;
            try { RawTranslate(t, v, space); return true; } catch { return false; }
        }

        /// <summary>按欧拉角旋转。</summary>
        internal static bool RotateEuler(Transform t, Vector3 euler, Space space)
        {
            if (t == null) return false;
            try { RawRotateEuler(t, euler, space); return true; } catch { return false; }
        }

        /// <summary>绕轴旋转。</summary>
        internal static bool RotateAxis(Transform t, Vector3 axis, float angle, Space space)
        {
            if (t == null) return false;
            try { RawRotateAxis(t, axis, angle, space); return true; } catch { return false; }
        }

        /// <summary>朝向世界坐标点。</summary>
        internal static bool LookAtPosition(Transform t, Vector3 worldPosition, Vector3 worldUp)
        {
            if (t == null) return false;
            try { RawLookAtPosition(t, worldPosition, worldUp); return true; } catch { return false; }
        }

        /// <summary>朝向另一个 Transform。</summary>
        internal static bool LookAtTransform(Transform t, Transform target)
        {
            if (t == null || target == null) return false;
            try { RawLookAtTransform(t, target); return true; } catch { return false; }
        }

        /// <summary>设置父节点。</summary>
        internal static bool SetParent(Transform t, Transform parent, bool worldPositionStays)
        {
            if (t == null) return false;
            try { RawSetParent(t, parent, worldPositionStays); return true; } catch { return false; }
        }

        /// <summary>子节点数量; 不可用时 0。</summary>
        internal static int ChildCount(Transform t)
        {
            if (t == null) return 0;
            try { return RawChildCount(t); } catch { return 0; }
        }

        /// <summary>按索引取子节点; 不可用/越界时 null。</summary>
        internal static Transform GetChild(Transform t, int index)
        {
            if (t == null || index < 0) return null;
            try { return RawGetChild(t, index); } catch { return null; }
        }

        /// <summary>按名字查找子节点; 不可用/找不到时 null。</summary>
        internal static Transform FindChild(Transform t, string name)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            try { return RawFindChild(t, name); } catch { return null; }
        }

        /// <summary>世界坐标转局部坐标; 不可用时 (0,0,0)。</summary>
        internal static Vector3 InverseTransformPoint(Transform t, Vector3 position)
        {
            if (t == null) return Vector3.zero;
            try { return RawInverseTransformPoint(t, position); } catch { return Vector3.zero; }
        }

        /// <summary>局部坐标转世界坐标; 不可用时 (0,0,0)。</summary>
        internal static Vector3 TransformPoint(Transform t, Vector3 position)
        {
            if (t == null) return Vector3.zero;
            try { return RawTransformPoint(t, position); } catch { return Vector3.zero; }
        }

        // =====================================================================
        // Object / GameObject
        // =====================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RawName(UnityEngine.Object o) { return o.name; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static GameObject RawGameObject(Component c) { return c.gameObject; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Transform RawTransform(GameObject go) { return go.transform; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Transform RawComponentTransform(Component c) { return c.transform; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetActive(GameObject go, bool value) { go.SetActive(value); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawActiveSelf(GameObject go) { return go.activeSelf; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawDestroy(UnityEngine.Object o) { UnityEngine.Object.Destroy(o); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static GameObject RawFind(string path) { return GameObject.Find(path); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static GameObject RawFindWithTag(string tag) { return GameObject.FindWithTag(tag); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static GameObject[] RawFindGameObjectsWithTag(string tag) { return GameObject.FindGameObjectsWithTag(tag); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static UnityEngine.Object[] RawFindObjectsOfTypeAll(Type type) { return Resources.FindObjectsOfTypeAll(type); }

        /// <summary>对象名; 不可用时 null。</summary>
        internal static string Name(UnityEngine.Object o)
        {
            if (o == null) return null;
            try { return RawName(o); } catch { return null; }
        }

        /// <summary>组件所属 GameObject; 不可用时 null。</summary>
        internal static GameObject GameObjectOf(Component c)
        {
            if (c == null) return null;
            try { return RawGameObject(c); } catch { return null; }
        }

        /// <summary>GameObject 的 Transform; 不可用时 null。</summary>
        internal static Transform TransformOf(GameObject go)
        {
            if (go == null) return null;
            try { return RawTransform(go); } catch { return null; }
        }

        /// <summary>组件的 Transform; 不可用时 null。</summary>
        internal static Transform TransformOf(Component c)
        {
            if (c == null) return null;
            try { return RawComponentTransform(c); } catch { return null; }
        }

        /// <summary>激活/失活对象; 不可用时返回 false。</summary>
        internal static bool SetActive(GameObject go, bool value)
        {
            if (go == null) return false;
            try { RawSetActive(go, value); return true; } catch { return false; }
        }

        /// <summary>对象自身是否激活; 不可用时 false。</summary>
        internal static bool ActiveSelf(GameObject go)
        {
            if (go == null) return false;
            try { return RawActiveSelf(go); } catch { return false; }
        }

        /// <summary>销毁对象; 不可用时返回 false。</summary>
        internal static bool DestroyObject(UnityEngine.Object o)
        {
            if (o == null) return false;
            try { RawDestroy(o); return true; } catch { return false; }
        }

        /// <summary>按路径查找; 不可用/找不到时 null。</summary>
        internal static GameObject Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return RawFind(path); } catch { return null; }
        }

        /// <summary>按 Tag 查找; 不可用/找不到时 null。</summary>
        internal static GameObject FindWithTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            try { return RawFindWithTag(tag); } catch { return null; }
        }

        /// <summary>按 Tag 查找全部; 不可用时空数组。</summary>
        internal static GameObject[] FindGameObjectsWithTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return new GameObject[0];
            try { return RawFindGameObjectsWithTag(tag) ?? new GameObject[0]; }
            catch { return new GameObject[0]; }
        }

        /// <summary>Resources.FindObjectsOfTypeAll; 不可用时空数组。</summary>
        internal static UnityEngine.Object[] FindObjectsOfTypeAll(Type type)
        {
            if (type == null) return new UnityEngine.Object[0];
            try { return RawFindObjectsOfTypeAll(type) ?? new UnityEngine.Object[0]; }
            catch { return new UnityEngine.Object[0]; }
        }

        // =====================================================================
        // Quaternion(纯计算, 但仍然是 ECall)
        // =====================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Quaternion RawEuler(float x, float y, float z) { return Quaternion.Euler(x, y, z); }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Quaternion RawLookRotation(Vector3 forward, Vector3 up) { return Quaternion.LookRotation(forward, up); }

        /// <summary>欧拉角转四元数; 不可用时 identity。</summary>
        internal static Quaternion Euler(float x, float y, float z)
        {
            try { return RawEuler(x, y, z); } catch { return Quaternion.identity; }
        }

        /// <summary>欧拉角(Vector3)转四元数。</summary>
        internal static Quaternion Euler(Vector3 euler) { return Euler(euler.x, euler.y, euler.z); }

        /// <summary>朝向; 不可用时 identity。</summary>
        internal static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            try { return RawLookRotation(forward, up); } catch { return Quaternion.identity; }
        }

        // =====================================================================
        // Application / Scene
        // =====================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RawUnityVersion() { return Application.unityVersion; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RawAppVersion() { return Application.version; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RawProductName() { return Application.productName; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static RuntimePlatform RawPlatform() { return Application.platform; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RawPersistentDataPath() { return Application.persistentDataPath; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawIsPlaying() { return Application.isPlaying; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RawActiveSceneName() { return SceneManager.GetActiveScene().name; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawActiveSceneHandle() { return SceneManager.GetActiveScene().handle; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawSceneCount() { return SceneManager.sceneCount; }

        /// <summary>Unity 版本; 不可用时 null。</summary>
        internal static string UnityVersion() { try { return RawUnityVersion(); } catch { return null; } }

        /// <summary>游戏版本; 不可用时 null。</summary>
        internal static string AppVersion() { try { return RawAppVersion(); } catch { return null; } }

        /// <summary>产品名; 不可用时 null。</summary>
        internal static string ProductName() { try { return RawProductName(); } catch { return null; } }

        /// <summary>运行平台; 不可用时返回 -1 的等价(WindowsPlayer 之外的未知值由调用方判断)。</summary>
        internal static bool TryGetPlatform(out RuntimePlatform platform)
        {
            try { platform = RawPlatform(); return true; }
            catch { platform = RuntimePlatform.WindowsPlayer; return false; }
        }

        /// <summary>持久化数据目录; 不可用时 null。</summary>
        internal static string PersistentDataPath() { try { return RawPersistentDataPath(); } catch { return null; } }

        /// <summary>是否在播放; 不可用时 false。</summary>
        internal static bool IsPlaying() { try { return RawIsPlaying(); } catch { return false; } }

        /// <summary>
        /// 是否运行在 Windows 上。
        /// Unity 平台 API 不可用时退回 .NET 自身的判定 —— 诊断信息不该因为 Unity 还没起来就变成"未知平台"。
        /// </summary>
        internal static bool IsWindows()
        {
            RuntimePlatform platform;
            if (TryGetPlatform(out platform))
                return platform == RuntimePlatform.WindowsPlayer || platform == RuntimePlatform.WindowsEditor;

            try { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
            catch { return false; }
        }

        /// <summary>当前场景名; 不可用时 null。</summary>
        internal static string ActiveSceneName() { try { return RawActiveSceneName(); } catch { return null; } }

        /// <summary>当前场景句柄; 不可用时 0。</summary>
        internal static int ActiveSceneHandle() { try { return RawActiveSceneHandle(); } catch { return 0; } }

        /// <summary>已加载场景数; 不可用时 0。</summary>
        internal static int SceneCount() { try { return RawSceneCount(); } catch { return 0; } }

        // =====================================================================
        // 其余 Object / Component / Camera 成员(同样逐个隔离)
        // =====================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetName(UnityEngine.Object o, string name) { o.name = name; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawInstanceId(UnityEngine.Object o) { return o.GetInstanceID(); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawDestroyWithDelay(UnityEngine.Object o, float delay) { UnityEngine.Object.Destroy(o, delay); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawDestroyImmediate(UnityEngine.Object o) { UnityEngine.Object.DestroyImmediate(o); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawDontDestroyOnLoad(UnityEngine.Object o) { UnityEngine.Object.DontDestroyOnLoad(o); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Component RawGetComponentOfGameObject(GameObject go, Type type) { return go.GetComponent(type); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Component RawGetComponentOfComponent(Component c, Type type) { return c.GetComponent(type); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Component RawAddComponent(GameObject go, Type type) { return go.AddComponent(type); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Component RawGetComponentInChildren(GameObject go, Type type, bool includeInactive) { return go.GetComponentInChildren(type, includeInactive); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Component RawGetComponentInParent(GameObject go, Type type) { return go.GetComponentInParent(type); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Component[] RawGetComponentsInChildren(GameObject go, Type type, bool includeInactive) { return go.GetComponentsInChildren(type, includeInactive); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static GameObject RawCreateGameObject(string name) { return new GameObject(name); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static GameObject RawCreateGameObjectWithComponents(string name, Type[] components) { return new GameObject(name, components); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawActiveInHierarchy(GameObject go) { return go.activeInHierarchy; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawSceneIsValid(GameObject go) { return go.scene.IsValid(); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawLayer(GameObject go) { return go.layer; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetLayer(GameObject go, int layer) { go.layer = layer; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RawTag(Component c) { return c.tag; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetTag(GameObject go, string tag) { go.tag = tag; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawCompareTag(GameObject go, string tag) { return go.CompareTag(tag); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Vector3 RawQuaternionEulerAngles(Quaternion q) { return q.eulerAngles; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetCameraOrthographic(Camera c, bool v) { c.orthographic = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawOrthographicSize(Camera c) { return c.orthographicSize; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetOrthographicSize(Camera c, float v) { c.orthographicSize = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetCameraEnabled(Camera c, bool v) { c.enabled = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetCameraDepth(Camera c, float v) { c.depth = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawCullingMask(Camera c) { return c.cullingMask; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetCullingMask(Camera c, int v) { c.cullingMask = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawClearFlags(Camera c) { return (int)c.clearFlags; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetClearFlags(Camera c, int v) { c.clearFlags = (CameraClearFlags)v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Color RawBackgroundColor(Camera c) { return c.backgroundColor; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetBackgroundColor(Camera c, Color v) { c.backgroundColor = v; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static float RawAspect(Camera c) { return c.aspect; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawSetAspect(Camera c, float v) { c.aspect = v; }

        /// <summary>设置对象名。</summary>
        internal static bool SetName(UnityEngine.Object o, string name)
        {
            if (o == null) return false;
            try { RawSetName(o, name); return true; } catch { return false; }
        }

        /// <summary>实例 ID; 不可用时 0。</summary>
        internal static int InstanceId(UnityEngine.Object o)
        {
            if (o == null) return 0;
            try { return RawInstanceId(o); } catch { return 0; }
        }

        /// <summary>延迟销毁。</summary>
        internal static bool DestroyWithDelay(UnityEngine.Object o, float delay)
        {
            if (o == null) return false;
            try { RawDestroyWithDelay(o, delay); return true; } catch { return false; }
        }

        /// <summary>立即销毁。</summary>
        internal static bool DestroyImmediate(UnityEngine.Object o)
        {
            if (o == null) return false;
            try { RawDestroyImmediate(o); return true; } catch { return false; }
        }

        /// <summary>跨场景保留。</summary>
        internal static bool DontDestroyOnLoad(UnityEngine.Object o)
        {
            if (o == null) return false;
            try { RawDontDestroyOnLoad(o); return true; } catch { return false; }
        }

        /// <summary>取组件(GameObject)。</summary>
        internal static Component GetComponent(GameObject go, Type type)
        {
            if (go == null || type == null) return null;
            try { return RawGetComponentOfGameObject(go, type); } catch { return null; }
        }

        /// <summary>取组件(Component)。</summary>
        internal static Component GetComponent(Component c, Type type)
        {
            if (c == null || type == null) return null;
            try { return RawGetComponentOfComponent(c, type); } catch { return null; }
        }

        /// <summary>加组件。</summary>
        internal static Component AddComponent(GameObject go, Type type)
        {
            if (go == null || type == null) return null;
            try { return RawAddComponent(go, type); } catch { return null; }
        }

        /// <summary>子节点里找组件。</summary>
        internal static Component GetComponentInChildren(GameObject go, Type type, bool includeInactive)
        {
            if (go == null || type == null) return null;
            try { return RawGetComponentInChildren(go, type, includeInactive); } catch { return null; }
        }

        /// <summary>父节点里找组件。</summary>
        internal static Component GetComponentInParent(GameObject go, Type type)
        {
            if (go == null || type == null) return null;
            try { return RawGetComponentInParent(go, type); } catch { return null; }
        }

        /// <summary>子节点里找全部组件。</summary>
        internal static Component[] GetComponentsInChildren(GameObject go, Type type, bool includeInactive)
        {
            if (go == null || type == null) return new Component[0];
            try { return RawGetComponentsInChildren(go, type, includeInactive) ?? new Component[0]; }
            catch { return new Component[0]; }
        }

        /// <summary>创建 GameObject; 不可用时 null。</summary>
        internal static GameObject CreateGameObject(string name)
        {
            try { return RawCreateGameObject(name); } catch { return null; }
        }

        /// <summary>创建带组件的 GameObject; 不可用时 null。</summary>
        internal static GameObject CreateGameObject(string name, Type[] components)
        {
            if (components == null || components.Length == 0) return CreateGameObject(name);
            try { return RawCreateGameObjectWithComponents(name, components); } catch { return null; }
        }

        /// <summary>层级中是否激活; 不可用时 false。</summary>
        internal static bool ActiveInHierarchy(GameObject go)
        {
            if (go == null) return false;
            try { return RawActiveInHierarchy(go); } catch { return false; }
        }

        /// <summary>是否属于有效场景(排除 Project 资产); 不可用时 false。</summary>
        internal static bool SceneIsValid(GameObject go)
        {
            if (go == null) return false;
            try { return RawSceneIsValid(go); } catch { return false; }
        }

        /// <summary>层; 不可用时 0。</summary>
        internal static int Layer(GameObject go)
        {
            if (go == null) return 0;
            try { return RawLayer(go); } catch { return 0; }
        }

        /// <summary>设置层。</summary>
        internal static bool SetLayer(GameObject go, int layer)
        {
            if (go == null) return false;
            try { RawSetLayer(go, layer); return true; } catch { return false; }
        }

        /// <summary>Tag(组件); 不可用时 null。</summary>
        internal static string Tag(Component c)
        {
            if (c == null) return null;
            try { return RawTag(c); } catch { return null; }
        }

        /// <summary>设置 Tag。</summary>
        internal static bool SetTag(GameObject go, string tag)
        {
            if (go == null) return false;
            try { RawSetTag(go, tag); return true; } catch { return false; }
        }

        /// <summary>比较 Tag; 不可用时 false。</summary>
        internal static bool CompareTag(GameObject go, string tag)
        {
            if (go == null || string.IsNullOrEmpty(tag)) return false;
            try { return RawCompareTag(go, tag); } catch { return false; }
        }

        /// <summary>四元数转欧拉角; 不可用时 (0,0,0)。</summary>
        internal static Vector3 ToEulerAngles(Quaternion q)
        {
            try { return RawQuaternionEulerAngles(q); } catch { return Vector3.zero; }
        }

        /// <summary>是否正交(设置)。</summary>
        internal static bool SetCameraOrthographic(Camera c, bool v)
        {
            if (c == null) return false;
            try { RawSetCameraOrthographic(c, v); return true; } catch { return false; }
        }

        /// <summary>正交尺寸; 不可用时 0。</summary>
        internal static float OrthographicSize(Camera c)
        {
            if (c == null) return 0f;
            try { return RawOrthographicSize(c); } catch { return 0f; }
        }

        /// <summary>设置正交尺寸。</summary>
        internal static bool SetOrthographicSize(Camera c, float v)
        {
            if (c == null) return false;
            try { RawSetOrthographicSize(c, v); return true; } catch { return false; }
        }

        /// <summary>设置组件启用。</summary>
        internal static bool SetCameraEnabled(Camera c, bool v)
        {
            if (c == null) return false;
            try { RawSetCameraEnabled(c, v); return true; } catch { return false; }
        }

        /// <summary>设置相机深度。</summary>
        internal static bool SetCameraDepth(Camera c, float v)
        {
            if (c == null) return false;
            try { RawSetCameraDepth(c, v); return true; } catch { return false; }
        }

        /// <summary>剔除遮罩; 不可用时 0。</summary>
        internal static int CullingMask(Camera c)
        {
            if (c == null) return 0;
            try { return RawCullingMask(c); } catch { return 0; }
        }

        /// <summary>设置剔除遮罩。</summary>
        internal static bool SetCullingMask(Camera c, int v)
        {
            if (c == null) return false;
            try { RawSetCullingMask(c, v); return true; } catch { return false; }
        }

        /// <summary>清屏标志; 不可用时 0。</summary>
        internal static int ClearFlags(Camera c)
        {
            if (c == null) return 0;
            try { return RawClearFlags(c); } catch { return 0; }
        }

        /// <summary>设置清屏标志。</summary>
        internal static bool SetClearFlags(Camera c, int v)
        {
            if (c == null) return false;
            try { RawSetClearFlags(c, v); return true; } catch { return false; }
        }

        /// <summary>背景色; 不可用时 default。</summary>
        internal static Color BackgroundColor(Camera c)
        {
            if (c == null) return default(Color);
            try { return RawBackgroundColor(c); } catch { return default(Color); }
        }

        /// <summary>设置背景色。</summary>
        internal static bool SetBackgroundColor(Camera c, Color v)
        {
            if (c == null) return false;
            try { RawSetBackgroundColor(c, v); return true; } catch { return false; }
        }

        /// <summary>宽高比; 不可用时 0。</summary>
        internal static float Aspect(Camera c)
        {
            if (c == null) return 0f;
            try { return RawAspect(c); } catch { return 0f; }
        }

        /// <summary>设置宽高比。</summary>
        internal static bool SetAspect(Camera c, float v)
        {
            if (c == null) return false;
            try { RawSetAspect(c, v); return true; } catch { return false; }
        }

        // =====================================================================
        // Scene / SceneManager
        // =====================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Scene RawGetActiveScene() { return SceneManager.GetActiveScene(); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Scene RawGetSceneAt(int index) { return SceneManager.GetSceneAt(index); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static Scene RawGetSceneByName(string name) { return SceneManager.GetSceneByName(name); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RawSceneName(Scene s) { return s.name; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string RawScenePath(Scene s) { return s.path; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawSceneHandle(Scene s) { return s.handle; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static int RawSceneBuildIndex(Scene s) { return s.buildIndex; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawSceneIsValidOf(Scene s) { return s.IsValid(); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool RawSceneIsLoadedOf(Scene s) { return s.isLoaded; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static GameObject[] RawSceneRootGameObjects(Scene s) { return s.GetRootGameObjects(); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RawHookSceneEvents(
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> onLoaded,
            UnityEngine.Events.UnityAction<Scene> onUnloaded,
            UnityEngine.Events.UnityAction<Scene, Scene> onActiveChanged)
        {
            if (onLoaded != null) SceneManager.sceneLoaded += onLoaded;
            if (onUnloaded != null) SceneManager.sceneUnloaded += onUnloaded;
            if (onActiveChanged != null) SceneManager.activeSceneChanged += onActiveChanged;
        }

        /// <summary>当前活动场景; 不可用时返回无效 Scene。</summary>
        internal static Scene GetActiveScene()
        {
            try { return RawGetActiveScene(); } catch { return default(Scene); }
        }

        /// <summary>按索引取场景; 不可用时返回无效 Scene。</summary>
        internal static Scene GetSceneAt(int index)
        {
            if (index < 0) return default(Scene);
            try { return RawGetSceneAt(index); } catch { return default(Scene); }
        }

        /// <summary>按名字取场景; 不可用时返回无效 Scene。</summary>
        internal static Scene GetSceneByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return default(Scene);
            try { return RawGetSceneByName(name); } catch { return default(Scene); }
        }

        /// <summary>场景名; 不可用时 null。</summary>
        internal static string SceneName(Scene s) { try { return RawSceneName(s); } catch { return null; } }

        /// <summary>场景资产路径; 不可用时 null。</summary>
        internal static string ScenePath(Scene s) { try { return RawScenePath(s); } catch { return null; } }

        /// <summary>场景句柄; 不可用时 0。</summary>
        internal static int SceneHandle(Scene s) { try { return RawSceneHandle(s); } catch { return 0; } }

        /// <summary>场景构建序号; 不可用时 -1。</summary>
        internal static int SceneBuildIndex(Scene s) { try { return RawSceneBuildIndex(s); } catch { return -1; } }

        /// <summary>场景是否有效; 不可用时 false。</summary>
        internal static bool SceneIsValid(Scene s) { try { return RawSceneIsValidOf(s); } catch { return false; } }

        /// <summary>场景是否已加载; 不可用时 false。</summary>
        internal static bool SceneIsLoaded(Scene s) { try { return RawSceneIsLoadedOf(s); } catch { return false; } }

        /// <summary>场景根对象; 不可用时空数组。</summary>
        internal static GameObject[] SceneRootGameObjects(Scene s)
        {
            try { return RawSceneRootGameObjects(s) ?? new GameObject[0]; }
            catch { return new GameObject[0]; }
        }

        /// <summary>挂钩 Unity 场景事件; 不可用时返回 false(调用方应退回轮询兜底)。</summary>
        internal static bool HookSceneEvents(
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> onLoaded,
            UnityEngine.Events.UnityAction<Scene> onUnloaded,
            UnityEngine.Events.UnityAction<Scene, Scene> onActiveChanged)
        {
            try { RawHookSceneEvents(onLoaded, onUnloaded, onActiveChanged); return true; }
            catch { return false; }
        }
    }
}
