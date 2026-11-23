using System;
using System.Collections.Generic;
using System.Globalization;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Speed;
using CesiumLoader.SDK.UserInterface;
using CesiumLoader.Shared.Speed;
using UnityEngine;

namespace SpeedHackMod
{
    /// <summary>
    /// 入口。原生加载器调用 <c>SpeedHackMod.ModEntry.Main()</c>(boot 线程, 非主线程)。
    /// </summary>
    public static class ModEntry
    {
        /// <summary>
        /// 启动延迟。等 Unity 时间和输入接口可用后再启动控制器。
        /// </summary>
        private const int StartupDelayMs = 10000;

        public static void Main()
        {
            ModBase.Run(new SpeedHackController(), StartupDelayMs);
        }
    }

    /// <summary>
    /// 用 Unity 时间缩放实现变速。Delete 在 1.0x 和上次倍率间切换，
    /// Alt + = / Alt + - 调整倍率并在停手后写回配置。
    /// 原生时钟可用时，确认其恢复到 1x 后才应用 Unity 倍率。
    /// </summary>
    public sealed class SpeedHackController : ModBase
    {
        // =====================================================================
        // 配置
        // =====================================================================
        private ModConfig _config;
        private double _activeSpeed = 2.0;                        // 开启时使用的倍率
        private double _speedStep = SpeedHack.DefaultSpeedStep;   // 每次调整的倍率
        private double _minSpeed = SpeedHack.MinSpeed;
        private double _maxSpeed = SpeedHack.MaxSpeed;
        private KeyCode _toggleKey = KeyCode.Delete;
        private KeyCode _speedUpKey = KeyCode.Equals;             // 需按住 Alt
        private KeyCode _speedDownKey = KeyCode.Minus;            // 需按住 Alt
        private bool _useNumpadKeys = true;                       // 小键盘 +/- 也认
        private bool _notify = true;                              // 屏幕上弹提示
        private bool _rememberSpeed = true;                       // 写回 config.json
        private bool _toggleRestoresSpeed = true;                 // toggleKey 再按一次 = 切回刚才的倍率
        private float _repeatInterval = 0.15f;                    // 按住连调的间隔(秒); 0 = 只调一次

        private const float SaveDelaySec = 1.2f;                  // 停手多久后落盘

        // =====================================================================
        // 运行态
        // =====================================================================
        private bool _ready;               // 已确认原生引擎可用或没有原生端口
        private float _nextProbeAt;        // 下次探测引擎的时刻
        private bool _holding;             // 上一帧是否按着 Alt+加/减键(用于"按下立即调一次")
        private float _nextRepeatAt;       // 连续调整的下一次时刻
        private bool _speedDirty;          // 倍率改过, 待写回配置
        private float _saveAt;             // 可以落盘的时刻
        private double _restoreSpeed;      // 被 toggleKey 压到 1.0x 之前的倍率(0 = 本次运行还没记过)
        private readonly FixedStepBudget _fixedSteps = new FixedStepBudget(
            UnityFixedStepAccess.ReadStep, UnityFixedStepAccess.ReadMaximum, UnityFixedStepAccess.WriteStep);
        private bool _fixedStepUnavailable;
        private float _nextPerformanceSyncAt;
        private bool _toggleWasHeld;
        private readonly UnitySpeedScale _unityScale = new UnitySpeedScale(UnityScaleAccess.Read, UnityScaleAccess.Write);
        private double _unityTargetSpeed = 1.0;
        private bool _unityActive;
        private bool _unityFailed;
        private bool _nativeResetRequested;
        private float _nextNativeResetAt;
        internal Func<double?> ReadNativeSpeed = NativeSpeedState.Read;
        // 安卓数据版没有原生端口，原生时钟按 1x 处理。
        private bool _nativeEngineAbsent;
        private int _unityNativeWaitPolls;
        private double _loggedUnityTarget = double.NaN;
        private readonly FairyUiTimeScaleBridge _uiAnimations = new FairyUiTimeScaleBridge();
        private bool _uiAnimationFailed;
        private bool _loggedUiAnimationSupport;
        private readonly IndependentAnimationBridge _independentAnimations = new IndependentAnimationBridge(
            UnityAnimationDiscovery.FindType, UnityAnimationDiscovery.Find, UnityAnimationDiscovery.FindActive);
        private int _lastIndependentCount;
        private int _lastWorldUiCount;

        public override string Version { get { return "3.0.0"; } }

        // =====================================================================
        // 生命周期
        // =====================================================================

        public override void OnInitialize()
        {
            _config = Config;
            _independentAnimations.Report = message => Log.Info(message);
            _independentAnimations.RegisterUiRoot = root => _uiAnimations.RegisterExternalRoot(root);
            LoadConfig();

            if (SpeedHack.IsAvailable) MarkReady();
            else if (!HasNativeSpeedPort())
            {
                // 没有原生引擎（安卓数据版）：unity 模式自己就能加速，不必等一个不会来的回执。
                _nativeEngineAbsent = true;
                Log.Warn("无 CESIUM_SPEED_PORT，按「原生时钟 1x」处理；Unity 模式直接生效");
                MarkReady();
            }
            else
            {
                // 引擎还没就绪或尚未收到 UDP 回执: 每秒重试, 不做变速
                _nextProbeAt = UnityTime.RealtimeSinceStartup + 1.0f;
                Log.Warn("正在等待原生时钟状态，每秒重试，期间暂停变速热键");
                Log.Warn("引擎诊断: " + SpeedHack.UnavailableReason);
            }

            // 输入不可用时热键必然没反应 —— 提前写清楚, 省得排查"按了没反应"
            Log.Info("输入后端: " + InputService.BackendName + " (可用=" + InputService.IsAvailable + ")");
            Log.Info("操作: " + _toggleKey + (_toggleRestoresSpeed
                         ? " 在 1.0x 与刚才的倍率之间切换"
                         : " 设为 1.0x(toggleRestoresSpeed=false 已关掉回切)") +
                     "; 按住 Alt + " + _speedUpKey + " 加速, Alt + " + _speedDownKey +
                     " 减速 (每次 " + F(_speedStep) + "x, 范围 " + F(_minSpeed) + "~" + F(_maxSpeed) + "x" +
                     (_rememberSpeed ? ", 停手 " + F(SaveDelaySec) + "s 后写回配置" : "") + ")");
            Log.Info("配置键: speed(Alt 调整的记忆倍率)/speedStep/minSpeed/maxSpeed/toggleKey/toggleRestoresSpeed/" +
                     "speedUpKey/speedDownKey/useNumpadKeys/rememberSpeed/repeatInterval/notify");
        }

        private void MarkReady()
        {
            _ready = true;

            // 启动时沿用加载器设定的基础倍率。没有基础倍率时使用配置中的记忆倍率。
            double nativeSpeed = SpeedHack.Speed;
            _unityTargetSpeed = SpeedHack.ClampSpeed(nativeSpeed > 1.0001 ? nativeSpeed : _activeSpeed, _minSpeed, _maxSpeed);
            _activeSpeed = _unityTargetSpeed;
            PollUnityBackend();

            Log.Info("变速热键已就绪: " + Describe());
            LogEffectiveConfiguration();
            Log.Info("固定更新优化: " + (!_fixedStepUnavailable ? "开启" : "关闭/不可用") +
                     " (按已确认倍率调整物理步长，1 倍时恢复)");
            Log.Info("引擎: " + SpeedHack.Describe());
            if (_notify) Notify("变速: 热键已就绪 (" + F(_unityTargetSpeed) + "x)", UiNotificationLevel.Success);
        }

        public override void OnUpdate()
        {
            try
            {
                if (!_ready)
                {
                    float now = UnityTime.RealtimeSinceStartup;
                    if (now < _nextProbeAt) return;
                    _nextProbeAt = now + 1.0f;

                    if (!SpeedHack.IsAvailable)
                    {
                        if (HasNativeSpeedPort()) return;
                        _nativeEngineAbsent = true;
                        Log.Info("无 CESIUM_SPEED_PORT，按原生时钟 1x 启用 Unity 变速");
                    }
                    MarkReady();
                }

                HandleHotkeys();
                if (UnityTime.RealtimeSinceStartup >= _nextPerformanceSyncAt) PollUnityBackend();
                UpdateUnityAnimations();
                FlushSaveIfDue();
            }
            catch (Exception e) { Log.ReportCrash("OnUpdate", e); }
        }

        public override void OnUnload()
        {
            // 退出/卸载: 把还没落盘的倍率补写一次(不改倍率 —— 进程都要结束了, 别再动引擎)
            FlushSaveNow();
            RestoreUnityScale();
            RestoreFixedSteps();
            Log.Info("变速已卸载");
        }

        public override void OnConfigChanged()
        {
            LoadConfig();
            _uiAnimationFailed = false;
            _fixedStepUnavailable = false;
            _unityFailed = false;
            if (_ready)
            {
                // 没有原生端口的设备可直接改 speed 调速。有原生端口时，热重载只约束当前目标。
                double target = _nativeEngineAbsent ? _activeSpeed : _unityTargetSpeed;
                // 临时关闭的 1x 不受加速下限限制。
                _unityTargetSpeed = target <= 1.0001 ? 1.0 : SpeedHack.ClampSpeed(target, _minSpeed, _maxSpeed);
                PollUnityBackend();
            }
            Log.Info("配置已热重载: " + Describe());
            LogEffectiveConfiguration();
        }

        private void LogEffectiveConfiguration()
        {
            Log.Info("生效配置: " + (_config != null && !string.IsNullOrEmpty(_config.Path) ? _config.Path : "(未知路径)") +
                     "，Unity 变速，原生时钟保持 1x" +
                     "，accelerateUiAnimations=true(锁定)，accelerateIndependentAnimations=true(锁定)，optimizeFixedUpdates=true(锁定)");
        }

        // =====================================================================
        // 热键
        // =====================================================================

        private void HandleHotkeys()
        {
            if (UiService.ShouldBlockGameInput) return;
            bool toggleHeld = InputService.IsKeyHeld(_toggleKey);
            if (InputService.IsKeyPressed(_toggleKey) && !_toggleWasHeld) ToggleSpeed();
            _toggleWasHeld = toggleHeld;
            if (!InputService.IsAvailable) return;

            // Alt 是"调整倍率"的修饰键: 和 FreeCameraMod 的 Ctrl+=/Ctrl+- 一样只读键盘
            bool alt = InputService.IsKeyHeld(KeyCode.LeftAlt) || InputService.IsKeyHeld(KeyCode.RightAlt);

            int dir = 0;
            if (alt)
            {
                bool up = InputService.IsKeyHeld(_speedUpKey) ||
                          (_useNumpadKeys && InputService.IsKeyHeld(KeyCode.KeypadPlus));
                bool down = InputService.IsKeyHeld(_speedDownKey) ||
                            (_useNumpadKeys && InputService.IsKeyHeld(KeyCode.KeypadMinus));

                if (up != down) dir = up ? 1 : -1;   // 同时按住 = 忽略, 免得来回横跳
            }

            if (dir == 0)
            {
                _holding = false;
                return;
            }

            float now = UnityTime.RealtimeSinceStartup;
            if (_holding && (_repeatInterval <= 0f || now < _nextRepeatAt)) return;

            _holding = true;
            _nextRepeatAt = now + (_repeatInterval > 0f ? _repeatInterval : 0.5f);
            Adjust(dir);
        }

        /// <summary>按一次热键调整倍率, 下一帧即生效。</summary>
        private void Adjust(int dir)
        {
            // 从 Unity 当前目标起算，临时切到 1.0x 后加速会从 1.0x 开始。
            double current = _unityTargetSpeed;
            double next = SpeedHack.StepSpeed(current, dir, _speedStep, _minSpeed, _maxSpeed);

            if (Math.Abs(next - current) < 0.0005)
            {
                // 已经顶到上下限: 只提示, 不改状态也不写盘
                Notify("变速: 已是极限 " + F(current) + "x", UiNotificationLevel.Info);
                return;
            }

            if (!SetUnityTarget(next)) return;

            _activeSpeed = next;      // 记住用户选的倍率(下次变速沿用)
            MarkSpeedDirty();
            Log.Info("倍率 " + F(current) + "x -> " + F(next) + "x");
            Notify("变速: " + F(next) + "x", UiNotificationLevel.Info);
        }

        /// <summary>在 1.0x 和上次加速倍率间切换，保持配置中的记忆倍率。</summary>
        private void ToggleSpeed()
        {
            double current = _unityTargetSpeed;

            if (Math.Abs(current - 1.0) < 0.0005)
            {
                // 已经在 1.0x: 这一次是"切回刚才的倍率"
                double back = _toggleRestoresSpeed ? UsableRestoreSpeed() : 0.0;
                if (!(back > 1.0))
                {
                    Notify("变速: 已是 1.0x", UiNotificationLevel.Info);
                    return;
                }

                if (!SetUnityTarget(back)) return;

                Log.Info("倍率 1.0x -> " + F(back) + "x (切回 " + _toggleKey + " 按下前的倍率)");
                Notify("变速: " + F(back) + "x (再按 " + _toggleKey + " 回到 1.0x)", UiNotificationLevel.Info);
                return;
            }

            // 记下当前 Unity 目标，再切到 1.0x。
            double previous = _restoreSpeed;
            _restoreSpeed = SpeedHack.ClampSpeed(current, _minSpeed, _maxSpeed);

            if (!SetUnityTarget(1.0))
            {
                _restoreSpeed = previous;   // 写失败就别记下一个没能生效的倍率
                return;
            }

            Log.Info("倍率 " + F(current) + "x -> 1.0x" +
                     (_toggleRestoresSpeed ? " (再按 " + _toggleKey + " 回到 " + F(_restoreSpeed) + "x)" : ""));
            Notify(_toggleRestoresSpeed
                       ? "变速: 1.0x (再按 " + _toggleKey + " 回到 " + F(_restoreSpeed) + "x)"
                       : "变速: 1.0x", UiNotificationLevel.Info);
        }

        /// <summary>切回时该用的倍率: 夹到当前范围; 不可用(没记过 / 低于 1.0x)时返回 0。</summary>
        private double UsableRestoreSpeed()
        {
            if (!(_restoreSpeed > 1.0)) return 0.0;
            double clamped = SpeedHack.ClampSpeed(_restoreSpeed, _minSpeed, _maxSpeed);
            return clamped > 1.0 ? clamped : 0.0;
        }

        private bool SetUnityTarget(double speed)
        {
            if (_unityFailed) return false;
            double previous = _unityTargetSpeed;
            _unityTargetSpeed = speed;
            if (!_unityActive || ApplyUnityScale()) return true;
            _unityTargetSpeed = previous;
            return false;
        }

        private static bool HasNativeSpeedPort()
        {
            try
            {
                return int.TryParse(Environment.GetEnvironmentVariable("CESIUM_SPEED_PORT"), out int port)
                    && port > 0 && port <= 65535;
            }
            catch { return false; }
        }

        private void PollUnityBackend()
        {
            if (_unityFailed) return;
            double? confirmed = ReadNativeSpeed();
            if (confirmed.HasValue) _nativeEngineAbsent = false;
            else if (_nativeEngineAbsent && !HasNativeSpeedPort()) confirmed = 1.0;
            if (!confirmed.HasValue)
            {
                _nextPerformanceSyncAt = UnityTime.RealtimeSinceStartup + 0.5f;
                RestoreUnityScale();
                RestoreFixedSteps();
                _unityActive = false;
                if (++_unityNativeWaitPolls == 10)
                    Log.Warn("无法读取有效的原生时钟状态，已暂停 Unity 加速，等待确认恢复 1x。");
                return;
            }
            double native = confirmed.Value;
            _nextPerformanceSyncAt = UnityTime.RealtimeSinceStartup + (float)(0.5 * Math.Max(1.0, native));
            if (native > 1.0001)
            {
                RestoreUnityScale();
                RestoreFixedSteps();
                _unityActive = false;
                float now = UnityTime.RealtimeSinceStartup;
                if (!_nativeResetRequested || now >= _nextNativeResetAt)
                {
                    _nativeResetRequested = SpeedHack.SetSpeed(1.0);
                    _nextNativeResetAt = now + (float)(2.0 * Math.Max(1.0, native));
                }
                if (++_unityNativeWaitPolls == 10)
                {
                    Log.Warn("原生时钟尚未确认恢复 1x，已暂停 Unity 加速，避免双重倍率。");
                    Notify("变速: 等待原生时钟恢复 1x", UiNotificationLevel.Warning);
                }
                return;
            }
            bool becomingActive = !_unityActive;
            _unityActive = true;
            _nativeResetRequested = false;
            _unityNativeWaitPolls = 0;
            if (ApplyUnityScale() && becomingActive)
                Log.Info("Unity 加速已生效: " + F(_unityTargetSpeed) + "x，原生时钟 1x");
        }

        private bool ApplyUnityScale()
        {
            try
            {
                _unityScale.Synchronize(_unityTargetSpeed);
                SynchronizePerformance(_unityTargetSpeed);
                if (_loggedUnityTarget != _unityTargetSpeed)
                {
                    Log.Info("Unity 目标 " + F(_unityTargetSpeed) + "x，timeScale=" + F(UnityScaleAccess.Read()) +
                             "，原生时钟已确认 1x");
                    _loggedUnityTarget = _unityTargetSpeed;
                }
                return true;
            }
            catch (Exception e)
            {
                _unityFailed = true;
                RestoreUnityScale();
                RestoreFixedSteps();
                Log.Warn("Unity 加速接口不可用，已暂停变速: " + e.GetType().Name + " " + e.Message);
                Notify("变速: Unity 接口不可用", UiNotificationLevel.Warning);
                return false;
            }
        }

        private void RestoreUnityScale()
        {
            _independentAnimations.Restore();
            _uiAnimations.Restore();
            try { _unityScale.Restore(); }
            catch (Exception e) { Log.Warn("恢复 Unity 倍率失败: " + e.GetType().Name + " " + e.Message); }
        }

        private void UpdateUnityAnimations()
        {
            if (!_unityActive || _unityFailed || _unityTargetSpeed <= 1.0001)
            {
                if (_independentAnimations.ChangedComponents > 0) _independentAnimations.Restore();
                if (_uiAnimations.HasState) _uiAnimations.Restore();
                return;
            }
            try
            {
                if (UnityScaleAccess.Read() <= 0f) { _uiAnimations.Restore(); _independentAnimations.Restore(); return; }
                _independentAnimations.Tick(UnityTime.RealtimeSinceStartup, _unityTargetSpeed);
                if (!_uiAnimationFailed)
                    _uiAnimations.Tick(UnityTime.RealtimeSinceStartup);
                if (_uiAnimations.ExternalRoots != _lastWorldUiCount)
                {
                    _lastWorldUiCount = _uiAnimations.ExternalRoots;
                    if (_lastWorldUiCount > 0) Log.Info("世界空间 UI 动画同步: " + _lastWorldUiCount + " 个 UIPanel（含战斗骰点面板）");
                }
                if (_independentAnimations.ChangedComponents != _lastIndependentCount)
                {
                    _lastIndependentCount = _independentAnimations.ChangedComponents;
                    Log.Info("独立动画时钟同步: 组件 " + _lastIndependentCount + "，静音 UI 视频 " +
                             _independentAnimations.ChangedVideos + "（视频最高 3x）");
                }
                if (!_loggedUiAnimationSupport && (_uiAnimations.TrackedTransitions > 0 || _uiAnimations.TrackedAnimations > 0))
                {
                    _loggedUiAnimationSupport = true;
                    Log.Info("UI 动画同步已生效: 转场 " + _uiAnimations.TrackedTransitions + "，序列动画 " +
                             _uiAnimations.TrackedAnimations + "（后续按帧分批发现）");
                }
            }
            catch (Exception e)
            {
                _uiAnimationFailed = true;
                _uiAnimations.Restore();
                Log.Warn("UI 动画倍率同步不可用，已暂停该项: " + e.GetType().Name + " " + e.Message);
            }
        }

        private void SynchronizePerformance(double actualSpeed)
        {
            if (_fixedStepUnavailable) return;
            try { _fixedSteps.Synchronize(actualSpeed); }
            catch (Exception e)
            {
                // A missing ECall must not throw and log once per rendered frame.
                RestoreFixedSteps();
                _fixedStepUnavailable = true;
                Log.Warn("固定更新优化不可用，已暂停；热键变速仍可用: " + e.GetType().Name + " " + e.Message);
            }
        }

        private void RestoreFixedSteps()
        {
            try { _fixedSteps.Restore(); }
            catch (Exception e) { Log.Warn("恢复固定更新步长失败: " + e.GetType().Name + " " + e.Message); }
        }

        // =====================================================================
        // 配置读写
        // =====================================================================

        private void LoadConfig()
        {
            if (_config == null) return;

            _activeSpeed = _config.GetDouble("speed", _activeSpeed);
            _speedStep = _config.GetDouble("speedStep", _speedStep);
            _minSpeed = _config.GetDouble("minSpeed", _minSpeed);
            _maxSpeed = _config.GetDouble("maxSpeed", _maxSpeed);

            _toggleKey = ParseKey(_config.GetString("toggleKey", null), _toggleKey);
            _speedUpKey = ParseKey(_config.GetString("speedUpKey", null), _speedUpKey);
            _speedDownKey = ParseKey(_config.GetString("speedDownKey", null), _speedDownKey);
            _useNumpadKeys = _config.GetBool("useNumpadKeys", _useNumpadKeys);
            _notify = _config.GetBool("notify", _notify);
            _rememberSpeed = _config.GetBool("rememberSpeed", _rememberSpeed);
            _toggleRestoresSpeed = _config.GetBool("toggleRestoresSpeed", _toggleRestoresSpeed);
            _repeatInterval = _config.GetFloat("repeatInterval", _repeatInterval);

            bool docsChanged = SpeedHackConfigDocs.Apply(_config);
            bool removedOptions = RemoveObsoleteOptions();

            // 夹紧范围。注意要放在**读完全部键之后**: SaveConfig 会把所有键整份重写,
            // 提前调用会把还没读到的键写回默认值(等于吃掉用户的配置)。
            bool boundsFixed = ClampConfigBounds();

            // 首次运行: 把默认值整份写出来, 省得用户去翻文档猜键名
            if (!_config.Has("speed") || !_config.Has("toggleKey"))
            {
                SaveConfig();
                Log.Info("已生成默认配置: " + (_config.Path ?? "(未知路径)"));
            }
            else if (boundsFixed)
            {
                // 老的 config.json 里可能留着 minSpeed=0.1(那时候还能减速) —— 一次性改掉,
                // 免得每次启动都警告、也免得用户以为"改小就能减速"。
                SaveConfig();
                Log.Info("配置里的倍率范围已按新规则纠正(下限 1.0, 不允许减速)");
            }
            else if (docsChanged || removedOptions)
            {
                _config.SaveIfDirty();
            }

            if (removedOptions)
                Log.Info("已从配置文件清理旧设置: accelerationMode / optimizeFixedUpdates / accelerateUiAnimations / accelerateIndependentAnimations");
        }

        private bool RemoveObsoleteOptions()
        {
            bool changed = _config.Remove("accelerationMode");
            changed |= _config.Remove("optimizeFixedUpdates");
            changed |= _config.Remove("accelerateUiAnimations");
            changed |= _config.Remove("accelerateIndependentAnimations");

            var comments = _config.Get<IDictionary<string, object>>(ModConfig.CommentsKey, null);
            if (comments == null) return changed;

            bool strippedComment = false;
            var cleaned = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in comments)
            {
                if (string.Equals(pair.Key, "accelerationMode", StringComparison.Ordinal) ||
                    string.Equals(pair.Key, "optimizeFixedUpdates", StringComparison.Ordinal) ||
                    string.Equals(pair.Key, "accelerateUiAnimations", StringComparison.Ordinal) ||
                    string.Equals(pair.Key, "accelerateIndependentAnimations", StringComparison.Ordinal))
                {
                    strippedComment = true;
                    continue;
                }
                cleaned[pair.Key] = pair.Value;
            }
            if (!strippedComment) return changed;

            if (cleaned.Count == 0) _config.Remove(ModConfig.CommentsKey);
            else _config.Set(ModConfig.CommentsKey, cleaned);
            return true;
        }

        /// <summary>
        /// 把 speed/minSpeed/maxSpeed 夹到合法区间。返回是否真的改动了值(用于决定要不要写回配置)。
        ///
        /// 硬下限 1.0: 手改 config.json 把 minSpeed 写成 0.2 也没用 —— SDK 的
        /// ClampSpeed/SetSpeed 与加载器都会再拦一次, 这里只是让日志/提示如实反映。
        /// </summary>
        private bool ClampConfigBounds()
        {
            bool changed = false;

            if (_minSpeed < SpeedHack.MinSpeed)
            {
                SdkLog.Warn("SpeedHack", "minSpeed=" + F(_minSpeed) + " 低于硬下限, 按 " +
                             F(SpeedHack.MinSpeed) + " 处理(不允许减速)");
                _minSpeed = SpeedHack.MinSpeed;
                changed = true;
            }

            if (_maxSpeed < _minSpeed) { _maxSpeed = _minSpeed; changed = true; }
            if (_maxSpeed > SpeedHack.HardMaxSpeed) { _maxSpeed = SpeedHack.HardMaxSpeed; changed = true; }

            if (_activeSpeed < _minSpeed) { _activeSpeed = _minSpeed; changed = true; }
            if (_activeSpeed > _maxSpeed) { _activeSpeed = _maxSpeed; changed = true; }

            return changed;
        }

        private void SaveConfig()
        {
            if (_config == null) return;
            try
            {
                _config.Set("speed", _activeSpeed);
                _config.Set("speedStep", _speedStep);
                _config.Set("minSpeed", _minSpeed);
                _config.Set("maxSpeed", _maxSpeed);
                _config.Set("toggleKey", _toggleKey.ToString());
                _config.Set("speedUpKey", _speedUpKey.ToString());
                _config.Set("speedDownKey", _speedDownKey.ToString());
                _config.Set("useNumpadKeys", _useNumpadKeys);
                _config.Set("notify", _notify);
                _config.Set("rememberSpeed", _rememberSpeed);
                _config.Set("toggleRestoresSpeed", _toggleRestoresSpeed);
                _config.Set("repeatInterval", _repeatInterval);
                _config.SaveIfDirty();
            }
            catch (Exception e) { Log.ReportCrash("SaveConfig", e); }
        }

        private void MarkSpeedDirty()
        {
            if (!_rememberSpeed) return;
            _speedDirty = true;
            _saveAt = UnityTime.RealtimeSinceStartup + SaveDelaySec;
        }

        private void FlushSaveIfDue()
        {
            if (!_speedDirty) return;
            if (UnityTime.RealtimeSinceStartup < _saveAt) return;
            FlushSaveNow();
        }

        /// <summary>立刻把待写的倍率落盘(按住连调时只会在这里写一次)。</summary>
        private void FlushSaveNow()
        {
            if (!_speedDirty) return;
            _speedDirty = false;
            SaveConfig();
            Log.Info("倍率 " + F(_activeSpeed) + "x 已写回配置" +
                     (_config != null && !string.IsNullOrEmpty(_config.Path) ? ": " + _config.Path : ""));
        }

        private static KeyCode ParseKey(string name, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            try
            {
                if (Enum.TryParse(name.Trim(), true, out KeyCode key) && key != KeyCode.None) return key;
            }
            catch { }
            SdkLog.Warn("SpeedHack", "配置里的按键名无效: " + name + " (用 " + fallback + " 代替)");
            return fallback;
        }

        // =====================================================================
        // 工具
        // =====================================================================

        private string Describe()
        {
            return "Unity 目标 " + F(_unityTargetSpeed) + "x，原生时钟 " + F(SpeedHack.Speed) +
                   "x (记忆倍率 " + F(_activeSpeed) + "x)";
        }

        private void Notify(string text, UiNotificationLevel level)
        {
            if (!_notify) return;
            try { UiService.Notify(text, level, 2f, Context); }
            catch (Exception e) { Log.ReportCrash("Notify", e); }
        }

        private static string F(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
