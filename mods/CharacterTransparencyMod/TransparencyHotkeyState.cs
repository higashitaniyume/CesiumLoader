using System;

namespace CharacterTransparencyMod
{
    /// <summary>
    /// 纯逻辑热键状态机，不依赖 Unity/FairyGUI，方便离线单元测试。
    /// 用“按下电平”而不是 Input.GetKeyDown 做边沿，掉帧时也不会漏掉一次按键。
    /// </summary>
    internal sealed class TransparencyHotkeyState
    {
        private bool _wasDown;
        private float _downAt;

        /// <summary>当前是否应使角色半透明。</summary>
        public bool Active { get; private set; }

        /// <summary>
        /// 更新状态。
        /// </summary>
        /// <param name="down">本帧热键是否按住。</param>
        /// <param name="now">单调递增的真实时间（秒）。</param>
        /// <param name="holdMode">true 为长按模式（按住半透明，松开恢复）；false 为点击切换模式。</param>
        /// <param name="holdSeconds">长按触发所需时长（秒），切换模式忽略。</param>
        /// <returns>本帧结束后是否应半透明。</returns>
        public bool Update(bool down, float now, bool holdMode, float holdSeconds)
        {
            if (!holdMode)
            {
                if (down && !_wasDown) Active = !Active;
                _wasDown = down;
                return Active;
            }

            if (down && !_wasDown) _downAt = now;

            if (!down)
            {
                Active = false;
                _wasDown = false;
                return false;
            }

            if (!Active && now - _downAt >= Math.Max(0f, holdSeconds) - 1e-4f) Active = true;
            _wasDown = true;
            return Active;
        }

        /// <summary>
        /// 清空状态（改键、改模式、禁用或卸载时调用）。
        /// </summary>
        /// <param name="keyDown">调用时热键是否仍按住。为 true 时不会把“还按着”误判成一次新的按下。</param>
        /// <param name="now">当前真实时间（秒），仅在 keyDown=true 时用于重新开始长按计时。</param>
        public void Reset(bool keyDown = false, float now = 0f)
        {
            Active = false;
            _wasDown = keyDown;
            _downAt = keyDown ? now : 0f;
        }
    }
}
