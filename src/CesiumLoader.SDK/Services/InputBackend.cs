using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 输入后端抽象(可测试 / 可替换)。
    /// </summary>
    public interface IInputBackend
    {
        /// <summary>后端名(诊断用)。</summary>
        string Name { get; }

        /// <summary>后端是否可用。</summary>
        bool IsAvailable { get; }

        /// <summary>按键是否按住。</summary>
        bool GetKey(KeyCode key);

        /// <summary>按键本帧是否按下。</summary>
        bool GetKeyDown(KeyCode key);

        /// <summary>按键本帧是否抬起。</summary>
        bool GetKeyUp(KeyCode key);

        /// <summary>鼠标按键是否按住(0=左, 1=右, 2=中)。</summary>
        bool GetMouseButton(int button);

        /// <summary>鼠标按键本帧是否按下。</summary>
        bool GetMouseButtonDown(int button);

        /// <summary>鼠标按键本帧是否抬起。</summary>
        bool GetMouseButtonUp(int button);

        /// <summary>读 Unity 轴(如 "Horizontal" / "Mouse X"); 不可用时返回 0。</summary>
        float GetAxis(string axisName);

        /// <summary>鼠标屏幕坐标。</summary>
        bool TryGetMousePosition(out Vector2 position);

        /// <summary>本帧滚轮增量。</summary>
        Vector2 GetMouseScrollDelta();

        /// <summary>
        /// 本帧鼠标/光标位移(像素)。后端无法提供时返回 false。
        ///
        /// 这是 Unity 的 "Mouse X"/"Mouse Y" 轴取不到值时的回退路径:
        ///   - <see cref="UnityInputReflectionBackend"/> 用 UnityEngine.Input.mousePosition 做位置差分
        ///     (HybridCLR 下 Win32 的 P/Invoke 不可用, 这条是唯一还能用的回退);
        ///   - <see cref="Win32InputBackend"/> 用 GetCursorPos 差分。
        /// 约定: 每帧只取一次; 同一帧内重复调用时后续调用返回 false(位移已被消费)。
        /// </summary>
        bool TryGetMouseDelta(out Vector2 delta);
    }

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

        /// <summary>滚轮增量。</summary>
        public Vector2 GetMouseScrollDelta()
        {
            if (_mouseScrollDelta == null) return Vector2.zero;
            try
            {
                object value = _mouseScrollDelta.GetValue(null, null);
                if (value is Vector2 v2) return v2;
                return Vector2.zero;
            }
            catch { return Vector2.zero; }
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

    /// <summary>
    /// 基于 Win32 <c>GetAsyncKeyState</c> 的后端。
    ///
    /// 用途: 当 UnityEngine.Input 反射不可用时仍然能读键盘/鼠标(硬件级状态)。
    /// 优点: 不依赖 Unity 输入系统; 缺点: 不遵循游戏内的按键重映射。
    /// </summary>
    public sealed class Win32InputBackend : IInputBackend
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private readonly HashSet<int> _held = new HashSet<int>();
        private readonly HashSet<int> _heldPrevious = new HashSet<int>();
        private readonly object _stateLock = new object();

        private static int _probeState; // 0 未知, 1 可用, -1 不可用
        private bool _tickRegistered;
        private int _lastX;
        private int _lastY;
        private bool _hasLastCursor;
        private Vector2 _accumulatedDelta;

        /// <summary>构造并注册每帧按键快照(用于 Down/Up 判定)。</summary>
        public Win32InputBackend()
        {
            if (IsAvailable) RegisterTick();
        }

        /// <summary>后端名。</summary>
        public string Name { get { return "Win32(GetAsyncKeyState)"; } }

        /// <summary>是否可用(首次调用时探测)。</summary>
        public bool IsAvailable
        {
            get
            {
                int state = Volatile.Read(ref _probeState);
                if (state != 0) return state > 0;

                try
                {
                    GetAsyncKeyState(0x01); // 探测一次
                    Volatile.Write(ref _probeState, 1);
                    return true;
                }
                catch (Exception e)
                {
                    Volatile.Write(ref _probeState, -1);
                    SdkLog.Warn("INPUT", "Win32 输入后端不可用: " + e.Message);
                    return false;
                }
            }
        }

        /// <summary>按键是否按住。</summary>
        public bool GetKey(KeyCode key)
        {
            int vk = ToVirtualKey(key);
            if (vk == 0) return false;
            try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
            catch { return false; }
        }

        /// <summary>本帧按下。</summary>
        public bool GetKeyDown(KeyCode key)
        {
            int vk = ToVirtualKey(key);
            if (vk == 0) return false;
            lock (_stateLock) { return _held.Contains(vk) && !_heldPrevious.Contains(vk); }
        }

        /// <summary>本帧抬起。</summary>
        public bool GetKeyUp(KeyCode key)
        {
            int vk = ToVirtualKey(key);
            if (vk == 0) return false;
            lock (_stateLock) { return !_held.Contains(vk) && _heldPrevious.Contains(vk); }
        }

        /// <summary>鼠标键按住。</summary>
        public bool GetMouseButton(int button)
        {
            return GetKey(MouseButtonToKeyCode(button));
        }

        /// <summary>鼠标键按下。</summary>
        public bool GetMouseButtonDown(int button)
        {
            return GetKeyDown(MouseButtonToKeyCode(button));
        }

        /// <summary>鼠标键抬起。</summary>
        public bool GetMouseButtonUp(int button)
        {
            return GetKeyUp(MouseButtonToKeyCode(button));
        }

        /// <summary>
        /// Win32 后端不知道 Unity 的轴配置; 只为最常见的两个轴提供基于按键的推导,
        /// 其余返回 0(这时应改用 <see cref="UnityInputReflectionBackend"/>)。
        /// </summary>
        public float GetAxis(string axisName)
        {
            if (string.IsNullOrEmpty(axisName)) return 0f;

            if (string.Equals(axisName, "Horizontal", StringComparison.OrdinalIgnoreCase))
            {
                float v = 0f;
                if (GetKey(KeyCode.A) || GetKey(KeyCode.LeftArrow)) v -= 1f;
                if (GetKey(KeyCode.D) || GetKey(KeyCode.RightArrow)) v += 1f;
                return v;
            }

            if (string.Equals(axisName, "Vertical", StringComparison.OrdinalIgnoreCase))
            {
                float v = 0f;
                if (GetKey(KeyCode.S) || GetKey(KeyCode.DownArrow)) v -= 1f;
                if (GetKey(KeyCode.W) || GetKey(KeyCode.UpArrow)) v += 1f;
                return v;
            }

            return 0f;
        }

        /// <summary>鼠标屏幕坐标。</summary>
        public bool TryGetMousePosition(out Vector2 position)
        {
            position = Vector2.zero;
            try
            {
                POINT point;
                if (!GetCursorPos(out point)) return false;

                // Unity 屏幕坐标原点在左下, Win32 在左上
                float height = 0f;
                height = UnityCall.ScreenHeight();
                position = new Vector2(point.X, height - point.Y);
                return true;
            }
            catch { return false; }
        }

        /// <summary>滚轮增量(Win32 无可靠轮询接口, 恒返回 0)。</summary>
        public Vector2 GetMouseScrollDelta()
        {
            return Vector2.zero;
        }

        /// <summary>
        /// 相对上次调用(或上次帧重置)的鼠标位移。
        /// 注意: 若鼠标被锁定/隐藏, 应改用 Unity 轴的 "Mouse X"/"Mouse Y"。
        /// </summary>
        public bool TryGetCursorDelta(out Vector2 delta)
        {
            delta = Vector2.zero;
            try
            {
                POINT point;
                if (!GetCursorPos(out point)) return false;

                if (!_hasLastCursor)
                {
                    _lastX = point.X;
                    _lastY = point.Y;
                    _hasLastCursor = true;
                    return false;
                }

                delta = new Vector2(point.X - _lastX, point.Y - _lastY);
                _lastX = point.X;
                _lastY = point.Y;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 本帧光标位移: 优先取本帧按键快照累计的位移(SnapshotKeys 已经差分过一次),
        /// 没有累计值时再直接读一次当前光标差分。
        /// </summary>
        public bool TryGetMouseDelta(out Vector2 delta)
        {
            delta = ConsumeCursorDelta();
            if (delta != Vector2.zero) return true;
            return TryGetCursorDelta(out delta);
        }

        private void RegisterTick()
        {
            if (_tickRegistered) return;
            _tickRegistered = true;

            try { UpdateService.SubscribeUpdate(SnapshotKeys, null, "Win32Input.Snapshot"); }
            catch (Exception e) { SdkLog.Debug("INPUT", "注册按键快照失败: " + e.Message); }
        }

        private void SnapshotKeys()
        {
            try
            {
                lock (_stateLock)
                {
                    _heldPrevious.Clear();
                    foreach (int vk in _held) _heldPrevious.Add(vk);
                    _held.Clear();

                    foreach (int vk in TrackedKeys)
                    {
                        try { if ((GetAsyncKeyState(vk) & 0x8000) != 0) _held.Add(vk); }
                        catch { }
                    }
                }

                Vector2 delta;
                if (TryGetCursorDelta(out delta))
                {
                    _accumulatedDelta += delta;
                }
            }
            catch { }
        }

        /// <summary>取出并清空自本帧开始累计的光标位移。</summary>
        public Vector2 ConsumeCursorDelta()
        {
            var delta = _accumulatedDelta;
            _accumulatedDelta = Vector2.zero;
            return delta;
        }

        private static KeyCode MouseButtonToKeyCode(int button)
        {
            switch (button)
            {
                case 0: return KeyCode.Mouse0;
                case 1: return KeyCode.Mouse1;
                case 2: return KeyCode.Mouse2;
                case 3: return KeyCode.Mouse3;
                case 4: return KeyCode.Mouse4;
                case 5: return KeyCode.Mouse5;
                case 6: return KeyCode.Mouse6;
                default: return KeyCode.None;
            }
        }

        /// <summary>快照需要跟踪的虚拟键(常用集合, 避免遍历 256 个键)。</summary>
        private static readonly int[] TrackedKeys = BuildTrackedKeys();

        private static int[] BuildTrackedKeys()
        {
            var keys = new List<int>();

            for (int vk = 0x30; vk <= 0x39; vk++) keys.Add(vk);            // 0-9
            for (int vk = 0x41; vk <= 0x5A; vk++) keys.Add(vk);            // A-Z
            for (int vk = 0x70; vk <= 0x7B; vk++) keys.Add(vk);            // F1-F12

            keys.Add(0x01); // 左键
            keys.Add(0x02); // 右键
            keys.Add(0x04); // 中键
            keys.Add(0x10); // Shift
            keys.Add(0x11); // Ctrl
            keys.Add(0x12); // Alt
            keys.Add(0x20); // Space
            keys.Add(0x09); // Tab
            keys.Add(0x0D); // Enter
            keys.Add(0x1B); // Esc
            keys.Add(0x25); // Left
            keys.Add(0x26); // Up
            keys.Add(0x27); // Right
            keys.Add(0x28); // Down
            keys.Add(0x21); // PageUp
            keys.Add(0x22); // PageDown
            keys.Add(0x24); // Home
            keys.Add(0x23); // End
            keys.Add(0x2D); // Insert
            keys.Add(0x2E); // Delete
            keys.Add(0x08); // Backspace
            keys.Add(0x14); // CapsLock
            keys.Add(0xBA); // ;
            keys.Add(0xBB); // =
            keys.Add(0xBC); // ,
            keys.Add(0xBD); // -
            keys.Add(0xBE); // .
            keys.Add(0xBF); // /
            keys.Add(0xC0); // `
            keys.Add(0xDB); // [
            keys.Add(0xDC); // backslash
            keys.Add(0xDD); // ]
            keys.Add(0xDE); // '

            return keys.ToArray();
        }

        /// <summary>KeyCode → Win32 虚拟键码(不支持返回 0)。</summary>
        public static int ToVirtualKey(KeyCode key)
        {
            int value = (int)key;

            if (value >= (int)KeyCode.A && value <= (int)KeyCode.Z)
                return value - 32; // 'a'(97) → VK_A(65)

            if (value >= (int)KeyCode.Alpha0 && value <= (int)KeyCode.Alpha9)
                return 0x30 + (value - (int)KeyCode.Alpha0);

            if (value >= (int)KeyCode.F1 && value <= (int)KeyCode.F12)
                return 0x70 + (value - (int)KeyCode.F1);

            switch (key)
            {
                case KeyCode.Space: return 0x20;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Return: return 0x0D;
                case KeyCode.Escape: return 0x1B;
                case KeyCode.Backspace: return 0x08;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.RightArrow: return 0x27;
                case KeyCode.LeftShift: case KeyCode.RightShift: return 0x10;
                case KeyCode.LeftControl: case KeyCode.RightControl: return 0x11;
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return 0x12;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.Home: return 0x24;
                case KeyCode.End: return 0x23;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.CapsLock: return 0x14;
                case KeyCode.Mouse0: return 0x01;
                case KeyCode.Mouse1: return 0x02;
                case KeyCode.Mouse2: return 0x04;
                case KeyCode.Semicolon: return 0xBA;
                case KeyCode.Equals: return 0xBB;
                case KeyCode.Comma: return 0xBC;
                case KeyCode.Minus: return 0xBD;
                case KeyCode.Period: return 0xBE;
                case KeyCode.Slash: return 0xBF;
                case KeyCode.BackQuote: return 0xC0;
                case KeyCode.LeftBracket: return 0xDB;
                case KeyCode.Backslash: return 0xDC;
                case KeyCode.RightBracket: return 0xDD;
                case KeyCode.Quote: return 0xDE;
                default: return 0;
            }
        }
    }

    /// <summary>
    /// 组合后端: 优先反射 UnityEngine.Input(遵循游戏输入设置),
    /// 不可用时回落到 Win32(硬件级)。
    /// </summary>
    public sealed class CompositeInputBackend : IInputBackend
    {
        private readonly IInputBackend _primary;
        private readonly IInputBackend _fallback;

        /// <summary>构造。</summary>
        public CompositeInputBackend(IInputBackend primary = null, IInputBackend fallback = null)
        {
            _primary = primary;
            _fallback = fallback;
        }

        /// <summary>后端名。</summary>
        public string Name
        {
            get
            {
                bool p = _primary != null && _primary.IsAvailable;
                bool f = _fallback != null && _fallback.IsAvailable;
                return "composite[primary=" + (p ? _primary.Name : "不可用") +
                       ", fallback=" + (f ? _fallback.Name : "不可用") + "]";
            }
        }

        /// <summary>任一后端可用。</summary>
        public bool IsAvailable
        {
            get { return (_primary != null && _primary.IsAvailable) || (_fallback != null && _fallback.IsAvailable); }
        }

        /// <summary>按键按住。</summary>
        public bool GetKey(KeyCode key)
        {
            if (Usable(_primary)) { bool v = _primary.GetKey(key); if (v) return true; }
            return Usable(_fallback) && _fallback.GetKey(key);
        }

        /// <summary>本帧按下。</summary>
        public bool GetKeyDown(KeyCode key)
        {
            if (Usable(_primary) && _primary.GetKeyDown(key)) return true;
            return Usable(_fallback) && _fallback.GetKeyDown(key);
        }

        /// <summary>本帧抬起。</summary>
        public bool GetKeyUp(KeyCode key)
        {
            if (Usable(_primary) && _primary.GetKeyUp(key)) return true;
            return Usable(_fallback) && _fallback.GetKeyUp(key);
        }

        /// <summary>鼠标键按住。</summary>
        public bool GetMouseButton(int button)
        {
            if (Usable(_primary) && _primary.GetMouseButton(button)) return true;
            return Usable(_fallback) && _fallback.GetMouseButton(button);
        }

        /// <summary>鼠标键按下。</summary>
        public bool GetMouseButtonDown(int button)
        {
            if (Usable(_primary) && _primary.GetMouseButtonDown(button)) return true;
            return Usable(_fallback) && _fallback.GetMouseButtonDown(button);
        }

        /// <summary>鼠标键抬起。</summary>
        public bool GetMouseButtonUp(int button)
        {
            if (Usable(_primary) && _primary.GetMouseButtonUp(button)) return true;
            return Usable(_fallback) && _fallback.GetMouseButtonUp(button);
        }

        /// <summary>读轴(优先主后端, 主后端返回 0 时用回落后端推导)。</summary>
        public float GetAxis(string axisName)
        {
            if (Usable(_primary))
            {
                float value = _primary.GetAxis(axisName);
                if (Math.Abs(value) > 0.0001f) return value;
            }
            return Usable(_fallback) ? _fallback.GetAxis(axisName) : 0f;
        }

        /// <summary>鼠标位置。</summary>
        public bool TryGetMousePosition(out Vector2 position)
        {
            position = Vector2.zero;
            if (Usable(_primary) && _primary.TryGetMousePosition(out position)) return true;
            return Usable(_fallback) && _fallback.TryGetMousePosition(out position);
        }

        /// <summary>滚轮增量。</summary>
        public Vector2 GetMouseScrollDelta()
        {
            if (Usable(_primary))
            {
                var value = _primary.GetMouseScrollDelta();
                if (value != Vector2.zero) return value;
            }
            return Usable(_fallback) ? _fallback.GetMouseScrollDelta() : Vector2.zero;
        }

        /// <summary>本帧光标位移: 先主后端, 主后端取不到时用回退后端。</summary>
        public bool TryGetMouseDelta(out Vector2 delta)
        {
            delta = Vector2.zero;
            if (Usable(_primary) && _primary.TryGetMouseDelta(out delta) && delta != Vector2.zero) return true;
            return Usable(_fallback) && _fallback.TryGetMouseDelta(out delta);
        }

        private static bool Usable(IInputBackend backend)
        {
            if (backend == null) return false;
            try { return backend.IsAvailable; }
            catch { return false; }
        }
    }
}
