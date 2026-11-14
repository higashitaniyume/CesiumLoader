using System;
using CesiumLoader.SDK.Mobile;

namespace FreeCameraMod
{
    /// <summary>
    /// 安卓触摸手势：把「捏合 / 单指拖动」接到原本给滚轮和方向键用的那套缩放/旋转逻辑上。
    ///
    /// 桌面那套（滚轮缩放、←/→ 旋转）在手机上根本没有输入源，`InputService` 在安卓上
    /// 也拿不到键盘鼠标，所以这里直接读 SDK 的 <see cref="MobileTouch"/>。
    /// </summary>
    public sealed partial class ZoomCameraController
    {
        /// <summary>捏合量（按屏幕宽归一化）换算成"滚轮格数"的系数。</summary>
        private const float TouchPinchToScroll = 12f;

        /// <summary>单指横拖一个屏宽等于转多少度。</summary>
        private const float TouchDragDegreesPerScreen = 90f;

        private void HandleTouchInput()
        {
            if (!MobileTouch.IsSupported) return;

            if (MobileTouch.TryGetPinch(out float pinch) && Math.Abs(pinch) > 0.0001f)
            {
                float scroll = (_invertWheel ? -pinch : pinch) * TouchPinchToScroll;
                HandleZoomScroll(scroll);
            }

            if (MobileTouch.TryGetDrag(out float dx, out _) && Math.Abs(dx) > 0.0001f)
            {
                ApplyTouchRotate(dx);
            }
        }

        /// <summary>单指横向拖动 = 旋转地图，方向和速度跟方向键一致（受 invertRotate 影响）。</summary>
        private void ApplyTouchRotate(float dx)
        {
            float direction = dx > 0f ? 1f : -1f;
            if (_invertRotate) direction = -direction;

            if (!TryBeginDrive("触摸旋转")) return;

            float delta = direction * Math.Abs(dx) * TouchDragDegreesPerScreen;
            if (Math.Abs(delta) < 0.01f) return;

            _targetYawDelta += delta;
            if (!_smooth) _yawDelta = _targetYawDelta;
        }
    }
}
