using System;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using UnityEngine;

namespace CesiumLoader.SDK.Mobile
{
    /// <summary>
    /// 安卓触摸输入（手势）入口。
    ///
    /// 为什么用反射：`UnityEngine.Input` 在 `UnityEngine.InputLegacyModule` 里，
    /// 热更侧编译期引用不到（桌面 SDK 走 <c>InputService</c> 的反射后端也是这个原因）。
    /// 这里只在构造函数里取一次 `Input` 的静态成员与 `Touch` 字段，之后每帧读值。
    ///
    /// 手势语义（mod 侧按这个用）：
    ///   - <see cref="TryGetDrag"/>：单指拖动，返回 -1..1 的归一化位移（按屏幕宽/高）；
    ///   - <see cref="TryGetPinch"/>：双指捏合，正 = 张开（放大），负 = 收拢；
    ///   - <see cref="UpdateCamera"/>：兼容旧签名，消费了手势返回 true（不直接改相机）。
    /// </summary>
    public static class MobileTouch
    {
        private static bool _resolved;
        private static MethodInfo _touchCountGetter;
        private static MethodInfo _getTouchMethod;
        private static FieldInfo _positionField;
        private static FieldInfo _deltaField;
        private static float _lastPinchDistance;

        private static object _camera;
        private static bool _warned;

        /// <summary>是否支持触摸（仅 Android 且有 Input 模块）。</summary>
        public static bool IsSupported
        {
            get
            {
                Resolve();
                return Application.platform == RuntimePlatform.Android && _touchCountGetter != null;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                Type input = null;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    input = assemblies[i].GetType("UnityEngine.Input", false);
                    if (input != null) break;
                }

                if (input == null)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        SdkLog.Warn("MobileTouch", "找不到 UnityEngine.Input，触摸手势不可用");
                    }
                    return;
                }

                _touchCountGetter = input.GetProperty("touchCount", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
                _getTouchMethod = input.GetMethod("GetTouch", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(int) }, null);
                if (_getTouchMethod == null) return;

                Type touch = _getTouchMethod.ReturnType;
                _positionField = touch.GetField("position", BindingFlags.Public | BindingFlags.Instance);
                _deltaField = touch.GetField("deltaPosition", BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception error)
            {
                SdkLog.Warn("MobileTouch", "解析触摸输入失败: " + error.Message);
            }
        }

        private static int TouchCount
        {
            get
            {
                Resolve();
                if (_touchCountGetter == null) return 0;
                try { return Convert.ToInt32(_touchCountGetter.Invoke(null, null)); }
                catch { return 0; }
            }
        }

        private static bool TryGetTouchAt(int index, out Vector2 position, out Vector2 delta)
        {
            position = Vector2.zero;
            delta = Vector2.zero;
            try
            {
                object touch = _getTouchMethod.Invoke(null, new object[] { index });
                if (touch == null || _positionField == null) return false;
                position = (Vector2)_positionField.GetValue(touch);
                if (_deltaField != null) delta = (Vector2)_deltaField.GetValue(touch);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>单指拖动：dx/dy 归一化到屏幕宽高（-1..1）。没有单指拖动返回 false。</summary>
        public static bool TryGetDrag(out float dx, out float dy)
        {
            dx = 0f;
            dy = 0f;
            if (TouchCount != 1) return false;
            if (!TryGetTouchAt(0, out _, out Vector2 delta)) return false;

            float width = Math.Max(1f, Screen.width);
            float height = Math.Max(1f, Screen.height);
            dx = delta.x / width;
            dy = delta.y / height;
            return Math.Abs(dx) > 0.0001f || Math.Abs(dy) > 0.0001f;
        }

        /// <summary>双指捏合：正 = 张开（放大），负 = 收拢。没有双指返回 false。</summary>
        public static bool TryGetPinch(out float delta)
        {
            delta = 0f;
            if (TouchCount < 2) { _lastPinchDistance = 0f; return false; }
            if (!TryGetTouchAt(0, out Vector2 first, out _)) return false;
            if (!TryGetTouchAt(1, out Vector2 second, out _)) return false;

            float distance = Vector2.Distance(first, second);
            if (_lastPinchDistance <= 0f)
            {
                _lastPinchDistance = distance;
                return false;
            }

            float width = Math.Max(1f, Screen.width);
            delta = (distance - _lastPinchDistance) / width;
            _lastPinchDistance = distance;
            return Math.Abs(delta) > 0.0001f;
        }

        /// <summary>兼容旧签名：绑定相机（本实现不直接改相机，只记录）。</summary>
        public static void AttachCamera(object camera)
        {
            _camera = camera;
        }

        /// <summary>兼容旧签名：有手势被消费返回 true。相机由 mod 自己按 TryGetDrag/TryGetPinch 驱动。</summary>
        public static bool UpdateCamera(object camera)
        {
            if (camera != null) _camera = camera;
            bool consumed = TryGetPinch(out _) || TryGetDrag(out _, out _);
            return consumed;
        }
    }
}
