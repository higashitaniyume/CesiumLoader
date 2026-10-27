using System;

namespace HideHudMod
{
    /// <summary>热键工作方式。</summary>
    internal enum HudPressMode
    {
        /// <summary>按住达到设定时长后隐藏，松开立即恢复。</summary>
        Hold,

        /// <summary>按一下切换隐藏/恢复。</summary>
        Toggle
    }

    /// <summary>
    /// 纯逻辑热键状态机，不依赖 Unity/FairyGUI，方便离线测试。
    ///
    /// 用“按下电平”而不是 Input.GetKeyDown 做边沿，掉帧时也不会漏掉一次按键。
    /// </summary>
    internal sealed class HudHotkeyState
    {
        private bool _wasDown;
        private float _downAt;

        /// <summary>当前是否应隐藏 HUD。</summary>
        public bool Hidden { get; private set; }

        /// <summary>
        /// 更新状态。
        /// </summary>
        /// <param name="down">本帧热键是否按住。</param>
        /// <param name="now">单调递增的真实时间（秒）。</param>
        /// <param name="mode">长按或切换。</param>
        /// <param name="holdSeconds">长按触发时长，切换模式忽略。</param>
        /// <returns>本帧结束后是否应隐藏。</returns>
        public bool Update(bool down, float now, HudPressMode mode, float holdSeconds)
        {
            if (mode == HudPressMode.Toggle)
            {
                if (down && !_wasDown) Hidden = !Hidden;
                _wasDown = down;
                return Hidden;
            }

            if (down && !_wasDown) _downAt = now;

            if (!down)
            {
                Hidden = false;
                _wasDown = false;
                return false;
            }

            if (!Hidden && now - _downAt >= Math.Max(0f, holdSeconds)) Hidden = true;
            _wasDown = true;
            return Hidden;
        }

        /// <summary>
        /// 清空状态（改键、改模式、禁用或卸载时调用）。
        /// </summary>
        /// <param name="keyDown">调用时热键是否仍按住。为 true 时不会把“还按着”误判成一次新的按下。</param>
        /// <param name="now">当前真实时间（秒），仅在 keyDown=true 时用于重新开始长按计时。</param>
        public void Reset(bool keyDown = false, float now = 0f)
        {
            Hidden = false;
            _wasDown = keyDown;
            _downAt = keyDown ? now : 0f;
        }
    }
}
