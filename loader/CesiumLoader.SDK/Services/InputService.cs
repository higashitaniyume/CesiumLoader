using System;
using System.Threading;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 输入服务。
    ///
    /// 后端策略: 优先反射 UnityEngine.Input(遵循游戏输入设置; 该类型位于
    /// InputLegacyModule, 本 SDK 编译期无法直接引用), 不可用时回落 Win32
    /// GetAsyncKeyState(硬件级, 无视游戏重映射)。两个后端都不可用时所有查询返回
    /// false/0 —— SDK 不会因此崩溃。
    ///
    /// 输入独占(Capture): 多个 mod 可能都想接管输入(例如两个自由相机)。
    /// Capture 是 SDK 级约定 + 光标锁定, 先到先得; 持有者的 mod 卸载时会自动释放。
    /// 注意: 它<b>不能</b>阻止游戏自身读取输入, 只能让其它 mod 礼让。
    /// </summary>
    public static class InputService
    {
        private static readonly object _lock = new object();

        private static IInputBackend _backend = new CompositeInputBackend(
            new UnityInputReflectionBackend(), new Win32InputBackend());

        private static ModContext _keyboardOwner;
        private static ModContext _mouseOwner;
        private static string _keyboardReason;
        private static string _mouseReason;

        private static bool _cursorHiddenByCapture;
        private static CursorLockMode _savedLockState = CursorLockMode.None;
        private static bool _savedCursorVisible = true;
        private static bool _cursorStateSaved;

        private static long _queryCount;
        private static long _captureGranted;
        private static long _captureDenied;

        // =====================================================================
        // 后端
        // =====================================================================

        /// <summary>当前输入后端。</summary>
        public static IInputBackend Backend { get { return _backend; } }

        /// <summary>替换输入后端(测试用)。传 null 恢复默认组合后端。</summary>
        public static void SetBackend(IInputBackend backend)
        {
            _backend = backend ?? new CompositeInputBackend(new UnityInputReflectionBackend(), new Win32InputBackend());
        }

        /// <summary>输入是否可用(两个后端都不可用时为 false)。</summary>
        public static bool IsAvailable
        {
            get
            {
                try { return _backend != null && _backend.IsAvailable; }
                catch { return false; }
            }
        }

        /// <summary>后端描述(诊断用)。</summary>
        public static string BackendName
        {
            get { try { return _backend != null ? _backend.Name : "(null)"; } catch { return "(异常)"; } }
        }

        /// <summary>输入查询次数。</summary>
        public static long QueryCount { get { return Interlocked.Read(ref _queryCount); } }

        /// <summary>授予过的独占次数。</summary>
        public static long CaptureGrantedCount { get { return Interlocked.Read(ref _captureGranted); } }

        /// <summary>被拒绝的独占请求次数。</summary>
        public static long CaptureDeniedCount { get { return Interlocked.Read(ref _captureDenied); } }

        // =====================================================================
        // 按键
        // =====================================================================

        /// <summary>键是否按住。</summary>
        public static bool IsKeyHeld(KeyCode key)
        {
            Interlocked.Increment(ref _queryCount);
            return Safe(() => _backend.GetKey(key));
        }

        /// <summary>键是否在本帧按下(等价 IsKeyDown)。</summary>
        public static bool IsKeyPressed(KeyCode key)
        {
            Interlocked.Increment(ref _queryCount);
            return Safe(() => _backend.GetKeyDown(key));
        }

        /// <summary>键是否在本帧抬起(等价 IsKeyUp)。</summary>
        public static bool IsKeyReleased(KeyCode key)
        {
            Interlocked.Increment(ref _queryCount);
            return Safe(() => _backend.GetKeyUp(key));
        }

        /// <summary>键是否在本帧按下(别名, 对齐 Unity 命名)。</summary>
        public static bool IsKeyDown(KeyCode key)
        {
            return IsKeyPressed(key);
        }

        /// <summary>键是否在本帧抬起(别名, 对齐 Unity 命名)。</summary>
        public static bool IsKeyUp(KeyCode key)
        {
            return IsKeyReleased(key);
        }

        /// <summary>任意一个键在本帧按下。</summary>
        public static bool IsAnyKeyDown(params KeyCode[] keys)
        {
            if (keys == null) return false;
            for (int i = 0; i < keys.Length; i++)
            {
                if (IsKeyPressed(keys[i])) return true;
            }
            return false;
        }

        // =====================================================================
        // 轴 / 鼠标
        // =====================================================================

        /// <summary>读 Unity 轴("Horizontal" / "Vertical" / "Mouse X" / "Mouse Y" 等)。</summary>
        public static float GetAxis(string axisName)
        {
            Interlocked.Increment(ref _queryCount);
            return SafeF(() => _backend.GetAxis(axisName));
        }

        /// <summary>键盘移动方向(X=Horizontal, Y=Vertical), 归一化到长度 ≤1。</summary>
        public static Vector2 GetMoveAxis()
        {
            float x = GetAxis("Horizontal");
            float y = GetAxis("Vertical");
            var v = new Vector2(x, y);
            if (v.sqrMagnitude > 1f) v = v.normalized;
            return v;
        }

        /// <summary>
        /// 鼠标本帧位移(优先 Unity 的 "Mouse X"/"Mouse Y" 轴)。
        ///
        /// 回退顺序:
        ///   1) Unity 轴(鼠标被锁定时最可靠);
        ///   2) 后端自带的光标位移 —— UnityEngine.Input.mousePosition 位置差分(Win32 P/Invoke
        ///      在 HybridCLR 下不可用, 所以位置差分是实际生效的那条)。
        /// 注意: 每帧只应调用一次, 位移会被消费。
        /// </summary>
        public static Vector2 GetMouseDelta()
        {
            Interlocked.Increment(ref _queryCount);

            // 1) Unity 轴(鼠标被锁定时仍然可用, 这是最可靠的方式)
            float mx = SafeF(() => _backend.GetAxis("Mouse X"));
            float my = SafeF(() => _backend.GetAxis("Mouse Y"));
            if (Math.Abs(mx) > 0.0001f || Math.Abs(my) > 0.0001f) return new Vector2(mx, my);

            // 2) 后端光标位移回退(复合后端会自己挑可用的那个后端)
            Vector2 delta;
            if (SafeTryDelta(out delta) && (Math.Abs(delta.x) > 0.0001f || Math.Abs(delta.y) > 0.0001f)) return delta;

            return Vector2.zero;
        }

        private static bool SafeTryDelta(out Vector2 delta)
        {
            delta = Vector2.zero;
            try
            {
                return _backend != null && _backend.TryGetMouseDelta(out delta);
            }
            catch { return false; }
        }

        /// <summary>鼠标滚轮本帧增量(y 为正表示向上滚)。</summary>
        public static float GetMouseScroll()
        {
            Interlocked.Increment(ref _queryCount);
            return SafeF(() => _backend.GetMouseScrollDelta().y);
        }

        /// <summary>鼠标屏幕坐标。</summary>
        public static bool TryGetMousePosition(out Vector2 position)
        {
            Interlocked.Increment(ref _queryCount);
            position = Vector2.zero;
            try { return _backend != null && _backend.TryGetMousePosition(out position); }
            catch { return false; }
        }

        /// <summary>鼠标键是否按住。</summary>
        public static bool IsMouseButtonHeld(int button)
        {
            Interlocked.Increment(ref _queryCount);
            return Safe(() => _backend.GetMouseButton(button));
        }

        /// <summary>鼠标键是否本帧按下。</summary>
        public static bool IsMouseButtonPressed(int button)
        {
            Interlocked.Increment(ref _queryCount);
            return Safe(() => _backend.GetMouseButtonDown(button));
        }

        /// <summary>鼠标键是否本帧抬起。</summary>
        public static bool IsMouseButtonReleased(int button)
        {
            Interlocked.Increment(ref _queryCount);
            return Safe(() => _backend.GetMouseButtonUp(button));
        }

        // =====================================================================
        // 输入独占
        // =====================================================================

        /// <summary>键盘是否被某个 mod 独占。</summary>
        public static bool IsKeyboardCaptured
        {
            get { lock (_lock) { return _keyboardOwner != null; } }
        }

        /// <summary>鼠标是否被某个 mod 独占。</summary>
        public static bool IsMouseCaptured
        {
            get { lock (_lock) { return _mouseOwner != null; } }
        }

        /// <summary>是否任一输入被独占。</summary>
        public static bool IsInputCaptured
        {
            get { return IsKeyboardCaptured || IsMouseCaptured; }
        }

        /// <summary>独占键盘的 mod 标识(没有则 null)。</summary>
        public static string KeyboardCaptureOwner
        {
            get { lock (_lock) { return _keyboardOwner != null ? _keyboardOwner.ModId : null; } }
        }

        /// <summary>独占鼠标的 mod 标识(没有则 null)。</summary>
        public static string MouseCaptureOwner
        {
            get { lock (_lock) { return _mouseOwner != null ? _mouseOwner.ModId : null; } }
        }

        /// <summary>
        /// 独占键盘。若已被其它 mod 独占且 <paramref name="force"/> 为 false, 返回 false。
        /// </summary>
        public static bool CaptureKeyboard(ModContext owner = null, bool force = false, string reason = null)
        {
            var resolved = owner ?? SafeCurrentContext();
            lock (_lock)
            {
                if (_keyboardOwner != null && _keyboardOwner != resolved && !force)
                {
                    Interlocked.Increment(ref _captureDenied);
                    SdkLog.Warn("INPUT", "键盘已被 " + _keyboardOwner.ModId + " 独占, 拒绝 " +
                                          (resolved != null ? resolved.ModId : "未知"));
                    return false;
                }

                _keyboardOwner = resolved;
                _keyboardReason = reason;
                Interlocked.Increment(ref _captureGranted);
            }

            if (resolved != null)
            {
                resolved.RegisterCleanup(() => ReleaseKeyboard(resolved));
                SdkLog.Info("INPUT", resolved.ModId + " 取得键盘独占" +
                                     (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"));
            }
            return true;
        }

        /// <summary>独占鼠标, 并锁定/隐藏光标(退出时自动还原)。</summary>
        public static bool CaptureMouse(ModContext owner = null, bool force = false, string reason = null)
        {
            var resolved = owner ?? SafeCurrentContext();
            lock (_lock)
            {
                if (_mouseOwner != null && _mouseOwner != resolved && !force)
                {
                    Interlocked.Increment(ref _captureDenied);
                    SdkLog.Warn("INPUT", "鼠标已被 " + _mouseOwner.ModId + " 独占, 拒绝 " +
                                          (resolved != null ? resolved.ModId : "未知"));
                    return false;
                }

                _mouseOwner = resolved;
                _mouseReason = reason;
                Interlocked.Increment(ref _captureGranted);
            }

            SaveAndLockCursor();

            if (resolved != null)
            {
                resolved.RegisterCleanup(() => ReleaseMouse(resolved));
                SdkLog.Info("INPUT", resolved.ModId + " 取得鼠标独占" +
                                     (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"));
            }
            return true;
        }

        /// <summary>同时独占键盘与鼠标。</summary>
        public static bool Capture(ModContext owner = null, bool keyboard = true, bool mouse = true,
            bool force = false, string reason = null)
        {
            bool ok = true;
            if (keyboard) ok &= CaptureKeyboard(owner, force, reason);
            if (mouse) ok &= CaptureMouse(owner, force, reason);
            return ok;
        }

        /// <summary>释放键盘独占(只有持有者可释放, <paramref name="force"/> 可强制)。</summary>
        public static void ReleaseKeyboard(ModContext owner = null, bool force = false)
        {
            var resolved = owner ?? SafeCurrentContext();
            bool released = false;

            lock (_lock)
            {
                if (_keyboardOwner == null) return;
                if (_keyboardOwner != resolved && !force) return;
                _keyboardOwner = null;
                _keyboardReason = null;
                released = true;
            }

            if (released) SdkLog.Info("INPUT", "键盘独占已释放");
        }

        /// <summary>释放鼠标独占并还原光标。</summary>
        public static void ReleaseMouse(ModContext owner = null, bool force = false)
        {
            var resolved = owner ?? SafeCurrentContext();
            bool released = false;

            lock (_lock)
            {
                if (_mouseOwner == null) return;
                if (_mouseOwner != resolved && !force) return;
                _mouseOwner = null;
                _mouseReason = null;
                released = true;
            }

            RestoreCursor();
            if (released) SdkLog.Info("INPUT", "鼠标独占已释放");
        }

        /// <summary>释放该 mod 的全部独占。</summary>
        public static void ReleaseAll(ModContext owner = null, bool force = false)
        {
            ReleaseKeyboard(owner, force);
            ReleaseMouse(owner, force);
        }

        /// <summary>强制释放全部独占(游戏退出/异常恢复用)。</summary>
        public static void ForceReleaseAll()
        {
            lock (_lock)
            {
                _keyboardOwner = null;
                _mouseOwner = null;
                _keyboardReason = null;
                _mouseReason = null;
            }
            RestoreCursor();
            SdkLog.Info("INPUT", "已强制释放全部输入独占");
        }

        // =====================================================================
        // 光标
        // =====================================================================

        /// <summary>显示/隐藏光标。</summary>
        public static bool SetCursorVisible(bool visible)
        {
            if (UnityCall.SetCursorVisible(visible)) return true;
            SdkLog.Debug("INPUT", "设置光标可见性失败: Unity 光标 API 不可用");
            return false;
        }

        /// <summary>设置光标锁定状态。</summary>
        public static bool SetCursorLockMode(CursorLockMode mode)
        {
            if (UnityCall.SetCursorLockState(mode)) return true;
            SdkLog.Debug("INPUT", "设置光标锁定失败: Unity 光标 API 不可用");
            return false;
        }

        /// <summary>保存当前光标状态并锁定+隐藏(供自由相机等使用)。</summary>
        public static bool SaveAndLockCursor()
        {
            try
            {
                if (!_cursorStateSaved)
                {
                    _savedLockState = UnityCall.CursorLockState();
                    _savedCursorVisible = UnityCall.CursorVisible();
                    _cursorStateSaved = true;
                }
                UnityCall.SetCursorLockState(CursorLockMode.Locked);
                UnityCall.SetCursorVisible(false);
                _cursorHiddenByCapture = true;
                return true;
            }
            catch (Exception e)
            {
                SdkLog.Debug("INPUT", "锁定光标失败: " + e.Message);
                return false;
            }
        }

        /// <summary>还原光标状态(仅当之前锁定过)。</summary>
        public static bool RestoreCursor()
        {
            try
            {
                if (!_cursorStateSaved || !_cursorHiddenByCapture) return false;
                UnityCall.SetCursorLockState(_savedLockState);
                UnityCall.SetCursorVisible(_savedCursorVisible);
                _cursorHiddenByCapture = false;
                _cursorStateSaved = false;
                return true;
            }
            catch (Exception e)
            {
                SdkLog.Debug("INPUT", "还原光标失败: " + e.Message);
                return false;
            }
        }

        /// <summary>当前光标锁定状态(诊断用)。</summary>
        public static string DescribeCursor()
        {
            return "lock=" + UnityCall.CursorLockState() + " visible=" + UnityCall.CursorVisible();
        }

        // =====================================================================
        // 内部
        // =====================================================================

        private static bool Safe(Func<bool> query)
        {
            try { return query(); }
            catch (Exception e)
            {
                SdkLog.Debug("INPUT", "输入查询异常: " + e.Message);
                return false;
            }
        }

        private static float SafeF(Func<float> query)
        {
            try { return query(); }
            catch (Exception e)
            {
                SdkLog.Debug("INPUT", "输入查询异常: " + e.Message);
                return 0f;
            }
        }

        private static ModContext SafeCurrentContext()
        {
            try { return ModContext.Current; }
            catch { return null; }
        }
    }
}
