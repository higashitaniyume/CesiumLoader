using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace CesiumLoader.SDK.Inputs
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
}
