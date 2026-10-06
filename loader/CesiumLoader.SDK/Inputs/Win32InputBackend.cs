using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using CesiumLoader.SDK.Internals;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Scheduling;

namespace CesiumLoader.SDK.Inputs
{
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

            // 小键盘数字: VK_NUMPAD0(0x60) ~ VK_NUMPAD9(0x69)
            if (value >= (int)KeyCode.Keypad0 && value <= (int)KeyCode.Keypad9)
                return 0x60 + (value - (int)KeyCode.Keypad0);

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
                case KeyCode.Mouse0: return 0x01;   // VK_LBUTTON
                case KeyCode.Mouse1: return 0x02;   // VK_RBUTTON
                case KeyCode.Mouse2: return 0x04;   // VK_MBUTTON
                case KeyCode.Mouse3: return 0x05;   // VK_XBUTTON1(侧键"后退")
                case KeyCode.Mouse4: return 0x06;   // VK_XBUTTON2(侧键"前进")
                case KeyCode.KeypadMultiply: return 0x6A;
                case KeyCode.KeypadPlus: return 0x6B;
                case KeyCode.KeypadMinus: return 0x6D;
                case KeyCode.KeypadPeriod: return 0x6E;
                case KeyCode.KeypadDivide: return 0x6F;
                case KeyCode.KeypadEnter: return 0x0D;   // 与主回车同码
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
}
