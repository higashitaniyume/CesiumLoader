using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace CesiumLoader.SDK.Inputs
{
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
