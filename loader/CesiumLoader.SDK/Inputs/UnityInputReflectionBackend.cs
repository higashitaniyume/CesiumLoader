using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;

namespace CesiumLoader.SDK.Inputs
{
    /// <summary>
    /// 基于反射的 UnityEngine.Input 后端。
    ///
    /// 背景: <c>UnityEngine.Input</c> 位于 UnityEngine.InputLegacyModule 程序集,
    /// 本 SDK 编译期只有 CoreModule 引用, 因此无法直接调用 —— 改为运行时反射。
    /// 本游戏自身使用 <c>Input.GetAxis("Horizontal")</c>(见游戏内 FreeCameraObject),
    /// 所以这些方法在 IL2CPP 元数据里是存在的。
    /// </summary>
    public sealed class UnityInputReflectionBackend : IInputBackend
    {
        private static readonly object _lock = new object();
        private static bool _resolved;
        private static Type _inputType;
        private static MethodInfo _getKey, _getKeyDown, _getKeyUp;
        private static MethodInfo _getAxis, _getAxisRaw;
        private static MethodInfo _getMouseButton, _getMouseButtonDown, _getMouseButtonUp;
        private static PropertyInfo _mousePosition, _mouseScrollDelta;
        private float _lastMouseX, _lastMouseY;
        private bool _hasLastMouse;

        /// <summary>后端名。</summary>
        public string Name { get { return "UnityEngine.Input(reflection)"; } }

        /// <summary>是否可用。</summary>
        public bool IsAvailable
        {
            get
            {
                Resolve();
                return _inputType != null && _getKey != null;
            }
        }

        /// <summary>按键是否按住。</summary>
        public bool GetKey(KeyCode key)
        {
            return InvokeBool(_getKey, key);
        }

        /// <summary>本帧按下。</summary>
        public bool GetKeyDown(KeyCode key)
        {
            return InvokeBool(_getKeyDown, key);
        }

        /// <summary>本帧抬起。</summary>
        public bool GetKeyUp(KeyCode key)
        {
            return InvokeBool(_getKeyUp, key);
        }

        /// <summary>鼠标键按住。</summary>
        public bool GetMouseButton(int button)
        {
            return InvokeBoolInt(_getMouseButton, button);
        }

        /// <summary>鼠标键按下。</summary>
        public bool GetMouseButtonDown(int button)
        {
            return InvokeBoolInt(_getMouseButtonDown, button);
        }

        /// <summary>鼠标键抬起。</summary>
        public bool GetMouseButtonUp(int button)
        {
            return InvokeBoolInt(_getMouseButtonUp, button);
        }

        /// <summary>读轴。</summary>
        public float GetAxis(string axisName)
        {
            if (_getAxis == null || string.IsNullOrEmpty(axisName)) return 0f;
            try
            {
                object value = _getAxis.Invoke(null, new object[] { axisName });
                return value != null ? Convert.ToSingle(value) : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>鼠标屏幕坐标。</summary>
        public bool TryGetMousePosition(out Vector2 position)
        {
            position = Vector2.zero;
            if (_mousePosition == null) return false;
            try
            {
                object value = _mousePosition.GetValue(null, null);
                if (value is Vector3 v3) { position = new Vector2(v3.x, v3.y); return true; }
                return false;
            }
            catch { return false; }
        }

        /// <summary>滚轮增量(统一成"每格 ±1")。</summary>
        public Vector2 GetMouseScrollDelta()
        {
            // 1) 首选 mouseScrollDelta: Unity 原生就是"每格 ±1", 不受轴的灵敏度配置影响
            if (_mouseScrollDelta != null)
            {
                try
                {
                    if (_mouseScrollDelta.GetValue(null, null) is Vector2 v2 && v2 != Vector2.zero) return v2;
                }
                catch { }
            }

            // 2) 回退 Unity 轴 "Mouse ScrollWheel"(默认每格 ±0.1, 折算成格数)
            //    某些输入配置/环境下 mouseScrollDelta 恒为 0, 这条能兜住; 轴也取不到就返回 0。
            //    注意 Win32 后端没有滚轮实现(回去看返回值), 所以这里是滚轮唯一可能的来源。
            if (_getAxis != null)
            {
                try
                {
                    object value = _getAxis.Invoke(null, new object[] { "Mouse ScrollWheel" });
                    if (value is float f)
                    {
                        float notches = NormalizeScrollAxis(f);
                        if (notches != 0f) return new Vector2(0f, notches);
                    }
                }
                catch { }
            }

            return Vector2.zero;
        }

        /// <summary>
        /// 把 Unity 轴 "Mouse ScrollWheel" 的取值折算成"格数"。
        ///
        /// 轴的默认灵敏度是每格 0.1, 而 <c>mouseScrollDelta</c> 是每格 ±1 —— 统一成后者,
        /// 调用方(自由相机的 FOV / 距离 / 高度)才能共用同一套"每格多少米/多少度"的步进。
        /// 绝对值 ≥ 0.5 的直接当格数(轴被改过灵敏度, 或某些平台本来就给格数)。
        /// </summary>
        public static float NormalizeScrollAxis(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return Math.Abs(value) < 0.5f ? value * 10f : value;
        }

        /// <summary>
        /// 光标位移(UnityEngine.Input.mousePosition 差分)。
        ///
        /// 这是 "Mouse X"/"Mouse Y" 轴之外唯一的托管回退方式:
        /// 不需要 P/Invoke, 所以在 HybridCLR 下(不允许 managed→native 调用)依然可用。
        /// 局限: 光标被锁定或到达屏幕边缘时位置不再变化, 此时取不到位移。
        /// </summary>
        public bool TryGetMouseDelta(out Vector2 delta)
        {
            delta = Vector2.zero;
            if (_mousePosition == null) return false;
            try
            {
                object value = _mousePosition.GetValue(null, null);
                float x, y;
                if (value is Vector3 v3) { x = v3.x; y = v3.y; }
                else if (value is Vector2 v2) { x = v2.x; y = v2.y; }
                else return false;

                if (!_hasLastMouse)
                {
                    _lastMouseX = x;
                    _lastMouseY = y;
                    _hasLastMouse = true;
                    return false;   // 第一帧只建立基准, 没有可用的位移
                }

                delta = new Vector2(x - _lastMouseX, y - _lastMouseY);
                _lastMouseX = x;
                _lastMouseY = y;
                return true;
            }
            catch { return false; }
        }

        /// <summary>清空位置差分基准(切场景/刚接管视角时调用, 避免一次巨大跳变)。</summary>
        public void ResetMouseDeltaBaseline()
        {
            _hasLastMouse = false;
        }

        private bool InvokeBool(MethodInfo method, KeyCode key)
        {
            if (method == null) return false;
            try
            {
                object value = method.Invoke(null, new object[] { key });
                return value is bool b && b;
            }
            catch { return false; }
        }

        private bool InvokeBoolInt(MethodInfo method, int button)
        {
            if (method == null) return false;
            try
            {
                object value = method.Invoke(null, new object[] { button });
                return value is bool b && b;
            }
            catch { return false; }
        }

        private static void Resolve()
        {
            if (Volatile.Read(ref _resolved)) return;
            lock (_lock)
            {
                if (_resolved) return;

                _inputType = RuntimeAssemblyService.FindType("UnityEngine.Input");
                if (_inputType != null)
                {
                    _getKey = RuntimeAssemblyService.FindMethod(_inputType, "GetKey", 1);
                    _getKeyDown = RuntimeAssemblyService.FindMethod(_inputType, "GetKeyDown", 1);
                    _getKeyUp = RuntimeAssemblyService.FindMethod(_inputType, "GetKeyUp", 1);
                    _getAxis = RuntimeAssemblyService.FindMethod(_inputType, "GetAxis", 1);
                    _getAxisRaw = RuntimeAssemblyService.FindMethod(_inputType, "GetAxisRaw", 1);
                    _getMouseButton = RuntimeAssemblyService.FindMethod(_inputType, "GetMouseButton", 1);
                    _getMouseButtonDown = RuntimeAssemblyService.FindMethod(_inputType, "GetMouseButtonDown", 1);
                    _getMouseButtonUp = RuntimeAssemblyService.FindMethod(_inputType, "GetMouseButtonUp", 1);
                    _mousePosition = RuntimeAssemblyService.FindProperty(_inputType, "mousePosition");
                    _mouseScrollDelta = RuntimeAssemblyService.FindProperty(_inputType, "mouseScrollDelta");
                }

                Volatile.Write(ref _resolved, true);
                SdkLog.Debug("INPUT", "UnityEngine.Input 反射解析: 类型=" + (_inputType != null) +
                                      " GetKey=" + (_getKey != null) +
                                      " GetAxis=" + (_getAxis != null));
            }
        }
    }
}
