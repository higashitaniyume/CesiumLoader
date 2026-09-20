using System;
using System.Globalization;
using CesiumLoader.SDK;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FreeCameraMod
{
    /// <summary>
    /// 入口。原生加载器调用 <c>FreeCameraMod.ModEntry.Main()</c>(boot 线程, 非主线程)。
    /// </summary>
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new FreeCameraController());
        }
    }

    /// <summary>
    /// 自由相机。三种模式(配置项 <c>mode</c>, 默认 <c>preset</c>):
    ///
    /// 【预设俯瞰模式 mode="preset"(默认, 推荐)】把相机固定到配置好的高处俯瞰视角,
    /// 用来一眼看完整个棋盘。<b>只绑定 F1 开关, 其它什么键都不绑</b>, 也不接管鼠标:
    ///   F1  在"游戏原视角"和"配置好的俯瞰视角"之间切换(退出时完整还原相机状态)
    ///   配置: height(相机绝对高度 Y) / pitch(俯角) / fov(视野) /
    ///         boardHeight(棋盘/桌面高度, 默认 0) / aimDistance(手动瞄准距离, 0 = 自动沿用游戏瞄准点)
    ///         autoActivate + autoActivateDelay(启动后延迟若干秒自动进入, 连 F1 都不用按)
    ///   几何: 相机 Y = height; 水平后退距离 = (height - 观察点Y) / tan(pitch),
    ///         所以相机永远正对观察点 —— 三个数字就完全确定视角。
    ///
    /// 【环绕观察模式 mode="orbit"】相机围绕"观察点"旋转(需要鼠标):
    ///   鼠标左右 = 绕观察点转圈; 鼠标上移 = 抬高相机俯视; 滚轮 = FOV;
    ///   Alt+滚轮 = 推拉距离; F2 = 复位
    ///
    /// 【飞行模式 mode="fly"】传统自由飞行(键盘可用):
    ///   WASD 前后左右 / Q·E 升降 / Shift 加速×4 / Ctrl 减速×0.25 / 鼠标转向 / 滚轮 FOV
    ///
    /// 游戏内实测(2026-09-20, AstralParty 国服), 两条决定了上面的设计:
    ///   1) 本游戏的 Unity "Mouse X/Y" 轴取不到值, 且"光标位移"回退后端不可用 —— 也就是
    ///      <b>mod 拿不到任何鼠标输入</b>(键盘正常, 所以 F1 能触发)。因此 orbit 模式的鼠标
    ///      转向在本游戏无效; preset 模式完全不依赖鼠标, 是本游戏唯一可靠的用法。
    ///   2) Cinemachine Brain 位于失活对象(/BattleShow(Clone)/PKCamera, enabled=False),
    ///      我们创建的 priority=100 VirtualCamera 不会驱动任何相机。所以 preset 模式
    ///      直接驱动 Camera.main, 并把写入放在 <see cref="OnLateUpdate"/>(PostLateUpdate 时机),
    ///      保证在游戏自己的 LateUpdate 之后生效, 不会被游戏每帧覆盖。
    ///
    /// 接管方式:
    ///   preset: 直接驱动 Camera.main, 每帧在 OnLateUpdate 写入。
    ///   orbit / fly: 1) 独立 CinemachineVirtualCamera(priority=100);
    ///                2) 回退: 临时禁用 CinemachineBrain, 直接驱动 Camera.main。
    ///
    /// 安全保证:
    ///   - 进入时保存完整的 CameraState + CinemachineBrainState;
    ///   - 退出/卸载/场景切换/相机被销毁 都会还原(含 enabled / 裁剪面 / FOV / 正交 / depth);
    ///   - 临时创建的 VirtualCamera 一定被销毁;
    ///   - 相机丢失或场景切换会自动重新绑定, 不缓存失效引用;
    ///   - 所有 Unity 访问都在主线程(SDK 的 OnUpdate / OnLateUpdate)。
    ///
    /// 配置: mods\FreeCameraMod\config.json
    ///   mode / height / pitch / fov / boardHeight / aimDistance /
    ///   orbitDistance / distanceStep / speed / rotationSpeed /
    ///   nearClip / farClip / invertY / toggleKey / resetKey / lockCursor
    /// </summary>
    public sealed class FreeCameraController : ModBase
    {
        // ---------------- 配置 ----------------
        private ModConfig _config;
        private string _cameraMode = "preset";  // "preset" 预设俯瞰 / "orbit" 环绕观察 / "fly" 自由飞行
        private float _presetHeight = 160f;     // preset: 相机绝对高度(Y)
        private float _presetPitch = 70f;       // preset: 俯角(度, >0 向下看)
        private float _presetFov = 80f;         // preset: 固定 FOV(越大视野越宽)
        private float _boardHeight;             // preset: 棋盘/桌面高度(Y), 默认 0 = 地面
        private float _aimDistance;             // preset: >0 = 手动指定瞄准距离; 0 = 自动(沿用游戏原本的瞄准点)
        private float _maxTargetDistance = 500f;// preset: 自动瞄准的交点距离上限(防止相机接近水平时算出天量距离)
        private bool _autoActivate;             // preset: 启动后自动进入俯瞰视角(不需要按 F1)
        private float _autoActivateDelay = 20f; // preset: 自动进入的延迟秒数(等游戏进入棋盘再接管)
        private float _orbitDistance = 12f;     // 进入时相机与观察点的距离
        private float _distanceStep = 1.5f;     // Alt+滚轮 每格推拉的距离
        private float _moveSpeed = 20f;
        private float _rotationSpeed = 2.0f;
        private float _defaultFov = 60f;
        private float _nearClip = 0.05f;
        private float _farClip = 2000f;
        private bool _invertY;
        private bool _lockCursor = true;
        private KeyCode _toggleKey = KeyCode.F1;
        private KeyCode _resetKey = KeyCode.F2;

        // ---------------- 运行态 ----------------
        private bool _active;
        private object _freeVcam;               // 独立 VirtualCamera(优先模式)
        private Camera _driven;                 // 回退模式直接驱动的相机
        private CameraState _savedState;        // 进入前的相机状态
        private CinemachineBrainState _savedBrain;
        private Vector3 _position;              // 飞行模式的相机位置
        private Vector3 _pivot;                 // 轨道模式: 观察点
        private float _distance;                // 轨道模式: 相机与观察点的当前距离
        private Vector3 _savedPivot;            // F2 复位用(进入时的观察点/距离)
        private float _savedDistance;
        private float _yaw;
        private float _pitch;                   // 轨道模式下即仰角(>0 = 相机在观察点上方俯视)
        private float _currentFov;
        private int _zeroMouseFrames;
        private bool _cursorCaptured;
        private string _mode = "(未激活)";
        private Vector3 _presetPose;             // preset: 算出的相机位置
        private Vector3 _presetTarget;           // preset: 算出的观察点(棋盘中心), 仅用于日志
        private float _presetUsedPitch;          // preset: 实际使用的俯角(已钳制)
        private float _presetUsedFov;            // preset: 实际使用的 FOV
        private string _presetNote = "";         // preset: 观察点来源/异常说明(日志用)
        private int _presetFrames;               // preset: 本次接管已写入的帧数(用于接管校验)
        private int _frameStartChecks;           // preset: 帧首校验计数
        private int _initTick;                   // 自动进入用的起始时刻
        private bool _autoActivateDone;          // 自动进入只触发一次
        private int _autoActivateFrames;         // 自动进入的就绪轮询计数
        private bool _autoActivateWarned;        // "相机未就绪" 只提示一次

        /// <summary>是否环绕观察模式。</summary>
        private bool IsOrbit
        {
            get { return string.Equals(_cameraMode, "orbit", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// 是否预设俯瞰模式: 只绑 F1 开关, 相机固定在配置好的高处俯视视角。
        /// 不接管鼠标(本游戏也拿不到鼠标输入), 所以不修改光标状态。
        /// </summary>
        private bool IsPreset
        {
            get { return string.Equals(_cameraMode, "preset", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>显示名。</summary>
        public override string Name { get { return "自由相机"; } }

        /// <summary>版本。</summary>
        public override string Version { get { return "2.1.0"; } }

        // =====================================================================
        // 生命周期
        // =====================================================================

        /// <summary>初始化(主线程)。</summary>
        public override void OnInitialize()
        {
            _config = Config;
            LoadConfig();
            _initTick = Environment.TickCount;

            // 相机变化时自动重新绑定 —— 回调在 SDK 内被 try/catch 保护
            CameraEvents.MainCameraLost += OnMainCameraLost;
            CameraEvents.MainCameraChanged += OnMainCameraChanged;

            var ctx = Context;
            if (ctx != null)
            {
                ctx.RegisterCleanup(() =>
                {
                    CameraEvents.MainCameraLost -= OnMainCameraLost;
                    CameraEvents.MainCameraChanged -= OnMainCameraChanged;
                });
            }

            Log.Info("自由相机已就绪: " + _toggleKey + " 开关, " + _resetKey + " 复位" +
                     " (模式=" + ModeName() +
                     (IsPreset
                         ? " height=" + F(_presetHeight) + " pitch=" + F(_presetPitch) +
                           " fov=" + F(_presetFov) + " boardHeight=" + F(_boardHeight) +
                           " aimDistance=" + F(_aimDistance)
                         : IsOrbit
                             ? " orbitDistance=" + F(_orbitDistance) + " distanceStep=" + F(_distanceStep)
                             : " speed=" + F(_moveSpeed)) +
                     " rotationSpeed=" + F(_rotationSpeed) +
                     " fov=" + F(_defaultFov) + " lockCursor=" + _lockCursor + ")");
            if (IsPreset)
            {
                Log.Info("操作: 只绑 " + _toggleKey + " 开关(其它按键/鼠标/滚轮均不接管). 视角由配置决定," +
                         " 改 config.json 后重启游戏生效");
                Log.Info("预设视角: 相机高度 Y=" + F(_presetHeight) + ", 俯角 " + F(_presetPitch) +
                         "°, FOV " + F(_presetFov) + ", 棋盘高度 Y=" + F(_boardHeight) +
                         (_aimDistance > 0.01f ? ", 手动瞄准距离 " + F(_aimDistance) + "m"
                                               : ", 棋盘中心=自动沿用游戏原本的瞄准点") +
                         (_autoActivate ? ", 将在 " + F(_autoActivateDelay) + "s 后自动进入" : ""));
            }
            else if (IsOrbit)
                Log.Info("操作: 鼠标=绕观察点转圈/上移抬高相机, 滚轮=FOV, Alt+滚轮=推拉距离, " +
                         _resetKey + "=复位");
            else
                Log.Info("操作: WASD=移动, Q/E=升降, 鼠标=转向, Shift=加速, Ctrl=减速, 滚轮=FOV, " +
                         _resetKey + "=复位");
            Log.Info("配置键: mode/height/pitch/fov/boardHeight/aimDistance/maxTargetDistance/" +
                     "autoActivate/autoActivateDelay/orbitDistance/distanceStep/speed/rotationSpeed/" +
                     "nearClip/farClip/invertY/toggleKey/resetKey/lockCursor");
        }

        /// <summary>模式显示名。</summary>
        private string ModeName()
        {
            if (IsPreset) return "预设俯瞰 preset";
            return IsOrbit ? "环绕观察 orbit" : "自由飞行 fly";
        }

        /// <summary>场景切换: 相机必然被销毁, 需要重新绑定。</summary>
        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_active) Rebind("场景切换 -> " + scene.name);
        }

        /// <summary>卸载: 务必还原相机。</summary>
        public override void OnUnload()
        {
            if (_active) Deactivate("mod 卸载");
            SaveConfig();
            Log.Info("自由相机已卸载");
        }

        /// <summary>每帧(主线程)。</summary>
        public override void OnUpdate()
        {
            try
            {
                // 自动进入预设视角(配置 autoActivate; 只在相机就绪后触发一次, 用户按过开关键后不再自动触发)
                if (!_active && !_autoActivateDone && _autoActivate && IsPreset)
                {
                    _autoActivateFrames++;
                    if (_autoActivateFrames % 30 == 1)
                    {
                        int elapsedMs = unchecked(Environment.TickCount - _initTick);
                        if (elapsedMs >= (int)(_autoActivateDelay * 1000f))
                        {
                            var cam = CameraService.GetMainCamera();
                            if (!UnityObject.IsAlive(cam) || IsPlaceholderCamera(cam))
                            {
                                // 菜单/加载阶段的相机是 (0,0,0) 占位相机: 现在接管只会算出一个没意义的
                                // 瞄准点, 等真正的游戏相机出现再进(进棋盘后主相机会被替换)。
                                if (!_autoActivateWarned)
                                {
                                    _autoActivateWarned = true;
                                    Log.Info("自动进入推迟: 主相机尚未就绪(占位相机), 等进入棋盘后再接管");
                                }
                            }
                            else
                            {
                                _autoActivateDone = true;
                                Log.Info("自动进入俯瞰视角 (autoActivate=true, 延迟 " + F(_autoActivateDelay) +
                                         "s, 相机已就绪); 按 " + _toggleKey + " 可随时关闭");
                                Activate();
                                return;
                            }
                        }
                    }
                }

                // 开关
                if (InputService.IsKeyPressed(_toggleKey))
                {
                    _autoActivateDone = true;   // 手动操作过就不再自动进入
                    if (_active) Deactivate("按键 " + _toggleKey);
                    else Activate();
                    return;
                }

                if (!_active) return;

                // 相机被销毁(切场景/换相机) -> 重新绑定, 绝不继续用失效引用
                if (_driven != null && !UnityObject.IsAlive(_driven)) { Rebind("驱动的相机被销毁"); return; }

                // 预设俯瞰模式: 不绑定其它按键, 相机由 OnLateUpdate 每帧保持。
                // 这里顺带做一次"帧首校验", 用来判定上一帧渲染是否真的用了预设视角。
                if (IsPreset)
                {
                    _frameStartChecks++;
                    if (_frameStartChecks == 60 || _frameStartChecks == 300) LogFrameStartCheck();
                    return;
                }

                if (InputService.IsKeyPressed(_resetKey)) { ResetToSaved(); return; }

                // UI 打开时不响应(避免"弹窗还在转视角")
                if (UiService.ShouldBlockGameInput) return;

                HandleInput();
            }
            catch (Exception e)
            {
                Log.ReportCrash("OnUpdate", e);
            }
        }

        /// <summary>
        /// 每帧 LateUpdate(主线程, 实际时机是 PostLateUpdate)。
        ///
        /// preset 模式的接管写在这里: 游戏的相机逻辑(自己的 LateUpdate 或 Cinemachine Brain)
        /// 跑完之后我们再写一遍, 所以渲染出来的必定是我们的预设视角 —— 这是本游戏里唯一
        /// 可靠的接管点(实测 Brain 在失活对象上, 单靠 VirtualCamera 不会生效)。
        /// 只写相机姿态, 不碰游戏逻辑, 退出时按保存的 CameraState 完整还原。
        /// </summary>
        public override void OnLateUpdate()
        {
            if (!_active || !IsPreset) return;
            try
            {
                ApplyPreset();
            }
            catch (Exception e)
            {
                Log.ReportCrash("OnLateUpdate", e);
            }
        }

        // =====================================================================
        // 开关
        // =====================================================================

        /// <summary>进入自由相机。</summary>
        public void Activate()
        {
            var camera = CameraService.GetMainCamera();
            if (!UnityObject.IsAlive(camera))
            {
                Log.Warn("当前没有可用主相机, 无法进入自由相机");
                UiService.Notify("自由相机: 未找到主相机", UiNotificationLevel.Warning, 3f, Context);
                return;
            }

            // 1) 保存原始状态(相机 + Cinemachine)
            _savedState = CameraService.CaptureState(camera);
            _savedBrain = CinemachineService.CaptureBrainState();

            _freeVcam = null;
            _driven = null;

            // 2) preset 模式: 直接驱动主相机。本游戏 Cinemachine Brain 位于失活对象,
            //    VirtualCamera 不会驱动任何相机, 所以这条路是唯一可靠的。
            if (IsPreset)
            {
                _driven = camera;
                _mode = "直接驱动主相机(预设俯瞰, PostLateUpdate 写入)";
            }
            // 2b) orbit/fly: 优先独立 CinemachineVirtualCamera(priority=100)
            else if (_savedBrain.Available && _savedBrain.BrainFound)
            {
                _freeVcam = CinemachineService.CreateVirtualCamera("CesiumFreeCamera", null, 100);
                if (_freeVcam != null)
                {
                    _mode = "Cinemachine VirtualCamera(priority=100)";
                    CameraService.SetActiveCamera(camera);
                }
            }

            // 3) 回退: 禁用 Brain, 直接驱动主相机
            if (_freeVcam == null && !IsPreset)
            {
                _driven = camera;
                _mode = "直接驱动主相机";
                if (_savedBrain.Available && _savedBrain.BrainFound)
                {
                    var brain = CinemachineService.FindBrain();
                    if (CinemachineService.SetBrainEnabled(brain, false))
                    {
                        _mode = "直接驱动主相机(已临时禁用 Cinemachine Brain)";
                        Log.Warn("无法创建 VirtualCamera, 已临时禁用 Brain 直改主相机");
                    }
                }
            }

            // 4) 从当前相机姿态起步
            _position = CameraService.GetPosition(camera);
            var euler = CameraService.GetEulerAngles(camera);
            _yaw = euler.y;
            _pitch = NormalizeAngle(euler.x);
            _currentFov = _savedState.FieldOfView > 0.1f ? _savedState.FieldOfView : _defaultFov;
            _zeroMouseFrames = 0;

            // 4b) 姿态: preset 算固定俯瞰位姿(并立刻应用一次); orbit 建立观察点
            if (IsPreset)
            {
                ComputePresetPose(camera);
                ApplyPreset();
            }
            else
            {
                SetupOrbit();
            }

            // 5) 鼠标独占(仅 orbit/fly 需要; preset 完全不接管鼠标, 游戏自己的鼠标操作照常)
            if (IsPreset)
            {
                _cursorCaptured = false;
            }
            else
            {
                _cursorCaptured = _lockCursor && InputService.CaptureMouse(Context, false, "自由相机");
                if (_lockCursor && !_cursorCaptured)
                    Log.Warn("鼠标已被其它 mod 独占, 自由相机将无法转向(仍可移动)");
            }

            _active = true;
            Log.Info("自由相机已开启 [接管: " + _mode + " / 模式: " + ModeName() + "]" +
                     (IsPreset
                         ? " 观察点=(" + F(_presetTarget.x) + "," + F(_presetTarget.y) + "," + F(_presetTarget.z) +
                           ") 相机=(" + F(_presetPose.x) + "," + F(_presetPose.y) + "," + F(_presetPose.z) +
                           ") 俯角=" + F(_presetUsedPitch) + " FOV=" + F(_presetUsedFov) +
                           (string.IsNullOrEmpty(_presetNote) ? "" : " [" + _presetNote + "]")
                         : IsOrbit
                             ? " 观察点=(" + F(_pivot.x) + "," + F(_pivot.y) + "," + F(_pivot.z) +
                               ") 距离=" + F(_distance) + " 仰角=" + F(_pitch)
                             : "") +
                     " 进入前状态: " + _savedState.Describe());
            UiService.Notify(IsPreset ? "自由相机: 俯瞰视角已开启 (" + _toggleKey + " 关闭)"
                                      : "自由相机: 开启 (" + _toggleKey + " 关闭)",
                UiNotificationLevel.Info, 2.5f, Context);
        }

        /// <summary>退出自由相机并还原。</summary>
        public void Deactivate(string reason)
        {
            if (!_active) return;
            _active = false;

            // 1) 还原 Cinemachine(同时销毁我们创建的 VirtualCamera)
            //    preset 模式从不碰 Brain/VirtualCamera(只驱动相机本体), 所以跳过, 避免
            //    去"还原"一个我们从未改过的 Brain。
            try
            {
                if (IsPreset)
                {
                    _freeVcam = null;
                }
                else if (_freeVcam != null)
                {
                    CinemachineService.RestoreBrainState(_savedBrain, _freeVcam);
                    _freeVcam = null;
                }
                else if (_savedBrain.Available && _savedBrain.BrainFound)
                {
                    CinemachineService.RestoreBrainState(_savedBrain, null);
                }
            }
            catch (Exception e) { Log.ReportCrash("Deactivate/Cinemachine", e); }

            // 2) 还原主相机完整状态
            try
            {
                var target = (_driven != null && UnityObject.IsAlive(_driven)) ? _driven : CameraService.GetMainCamera();
                if (UnityObject.IsAlive(target))
                {
                    bool ok = CameraService.RestoreState(target, _savedState);
                    Log.Info("相机状态还原" + (ok ? "完成" : "(部分失败, 见错误日志)"));
                }
                else
                {
                    Log.Warn("退出时找不到相机, 无法还原(相机可能已随场景销毁)");
                }
            }
            catch (Exception e) { Log.ReportCrash("Deactivate/Restore", e); }

            _driven = null;

            // 3) 释放输入独占
            if (_cursorCaptured)
            {
                InputService.ReleaseMouse(Context);
                _cursorCaptured = false;
            }
            CameraService.ClearActiveCamera();

            _mode = "(未激活)";
            Log.Info("自由相机已关闭 (" + reason + ")");
            UiService.Notify("自由相机: 关闭", UiNotificationLevel.Info, 1.5f, Context);
        }

        /// <summary>
        /// 轨道模式初始化: 观察点 = 相机正前方 <c>orbitDistance</c> 米处。
        /// <see cref="FreeCameraMath.OrbitPosition"/> 正是"观察点 - forward × distance",
        /// 所以在同一 yaw/仰角/距离下反推出的相机位置与当前相机位置重合 —— 进入时视角不跳,
        /// 按 F1 前后的画面完全一致。
        /// </summary>
        private void SetupOrbit()
        {
            float fx, fy, fz;
            FreeCameraMath.ForwardVector(_yaw, _pitch, out fx, out fy, out fz);

            _distance = FreeCameraMath.Clamp(_orbitDistance,
                FreeCameraMath.MinOrbitDistance, FreeCameraMath.MaxOrbitDistance);
            _pivot = new Vector3(_position.x + fx * _distance,
                                 _position.y + fy * _distance,
                                 _position.z + fz * _distance);

            _savedPivot = _pivot;
            _savedDistance = _distance;
        }

        // =====================================================================
        // 预设俯瞰模式
        // =====================================================================

        /// <summary>
        /// 预设俯瞰模式: 由配置算出固定视角。
        ///
        ///   棋盘中心(观察点) = 默认取"进入时相机正在看的那一点": 相机朝向射线与
        ///                      高度为 <c>boardHeight</c> 平面的交点。这样按 F1 只是
        ///                      "升高 + 拉远 + 视野变广", 视线中心不变, 不会看歪。
        ///                      <c>aimDistance</c> &gt; 0 时改为手动指定距离。
        ///   相机高度         = <c>height</c>(绝对 Y)
        ///   水平后退距离     = (height - 棋盘中心Y) / tan(pitch)  → 保证相机正对棋盘中心
        ///   朝向             = Euler(pitch, 进入时的 yaw, 0), 镜头 FOV = <c>fov</c>
        ///
        /// 也就是 height / pitch / fov 三个数字就完全确定视角: 相机永远正对棋盘中心,
        /// 不需要鼠标, 也不需要任何热键。
        /// </summary>
        private void ComputePresetPose(Camera camera)
        {
            Vector3 pos = CameraService.GetPosition(camera);
            var euler = CameraService.GetEulerAngles(camera);

            // 水平朝向沿用进入时的朝向(不做水平旋转); 俯角用配置值并钳制到合法范围
            _yaw = euler.y;
            float entryPitch = NormalizeAngle(euler.x);
            _presetUsedPitch = FreeCameraMath.Clamp(_presetPitch, 5f, 89f);
            _presetUsedFov = _presetFov > 1f ? _presetFov : _defaultFov;
            if (_presetUsedFov < 5f) _presetUsedFov = 5f;
            if (_presetUsedFov > 170f) _presetUsedFov = 170f;

            float yawRad = _yaw * (float)Math.PI / 180f;
            float hx = (float)Math.Sin(yawRad);   // 水平前方向(与 ForwardVector 同一约定)
            float hz = (float)Math.Cos(yawRad);

            _presetNote = "";

            // ---- 棋盘中心 ----
            float tx, tz;
            if (_aimDistance > 0.01f)
            {
                tx = pos.x + hx * _aimDistance;
                tz = pos.z + hz * _aimDistance;
                _presetNote = "手动瞄准 " + F(_aimDistance) + "m";
            }
            else
            {
                float fx, fy, fz;
                FreeCameraMath.ForwardVector(_yaw, entryPitch, out fx, out fy, out fz);

                float down = -fy;                 // >0 = 相机朝下看
                if (down < 0.15f)
                {
                    down = 0.15f;                 // 相机几乎水平: 交点会跑到天边, 钳制
                    _presetNote = "相机接近水平, 瞄准距离已钳制";
                }

                float t = (pos.y - _boardHeight) / down;
                if (t < 1f) t = 1f;
                if (t > _maxTargetDistance)
                {
                    t = _maxTargetDistance;
                    _presetNote = "瞄准距离超过上限 " + F(_maxTargetDistance);
                }
                tx = pos.x + fx * t;
                tz = pos.z + fz * t;
            }

            _presetTarget = new Vector3(tx, _boardHeight, tz);
            _presetFrames = 0;   // 接管校验重新计数
            _frameStartChecks = 0;

            // ---- 相机位姿: 高度固定, 水平后退 dy/tan(pitch), 正好俯视棋盘中心 ----
            float dy = _presetHeight - _presetTarget.y;
            float tan = (float)Math.Tan(_presetUsedPitch * Math.PI / 180.0);
            float back = tan > 0.0001f ? dy / tan : 0f;
            if (back < 1f) back = 1f;   // 高度不足时至少退开 1 米, 免得相机陷进棋盘中心

            _presetPose = new Vector3(tx - hx * back, _presetHeight, tz - hz * back);

            if (_presetNote.Length > 0) _presetNote += "; ";
            _presetNote += "相机在棋盘中心后方 " + F(back) + "m";
        }

        /// <summary>
        /// preset 模式的每帧写入: 把相机摆到预设位姿。
        ///
        /// 只写位姿与镜头参数(位置/朝向/FOV/裁剪面), 不动 enabled / depth / tag / 游戏逻辑。
        /// 调用点在 <see cref="OnLateUpdate"/>(PostLateUpdate 时机) —— 游戏自己的相机逻辑
        /// (含 Cinemachine Brain) 都跑完之后再写, 所以渲染出来的必定是预设视角。
        /// </summary>
        private void ApplyPreset()
        {
            var camera = (_driven != null && UnityObject.IsAlive(_driven)) ? _driven : CameraService.GetMainCamera();
            if (!UnityObject.IsAlive(camera))
            {
                // 相机没了: 由 OnUpdate 的 Rebind 负责重新绑定, 这里静默(否则每帧刷日志)
                return;
            }
            _driven = camera;

            // 写入(每帧) —— 在 OnLateUpdate(PostLateUpdate 时机)调用, 位于游戏自己的
            // LateUpdate 之后, 所以本帧渲染用的就是这里的位姿。
            CameraService.SetPosition(camera, _presetPose);
            CameraService.SetRotation(camera, Quaternion.Euler(_presetUsedPitch, _yaw, 0f));
            CameraService.SetFieldOfView(camera, _presetUsedFov);
            CameraService.SetNearClipPlane(camera, _nearClip);
            CameraService.SetFarClipPlane(camera, _farClip);

            // 接管校验: 写入之后立刻回读, 确认真的写进去了
            _presetFrames++;
            if (_presetFrames == 60 || _presetFrames == 300) LogWriteBackCheck(camera);
        }

        /// <summary>
        /// 写入回读校验: 写完立刻读回相机位姿。
        /// 偏差 ≈ 0 说明写入生效(相机确实在预设位姿上); 偏差大说明写入被拒绝/被立刻改回。
        /// 注意: 游戏每帧会先按自己的逻辑摆一次相机, 我们在 PostLateUpdate 再覆盖,
        /// 所以"游戏又改了相机"本身是正常的, 不代表失败 —— 判定生效与否只看这里的回读。
        /// </summary>
        private void LogWriteBackCheck(Camera camera)
        {
            Vector3 actual = CameraService.GetPosition(camera);
            var euler = CameraService.GetEulerAngles(camera);
            float err = Distance(actual, _presetPose);

            Log.Info("接管校验(第" + _presetFrames + "帧): 写入后实际 pos=(" + F(actual.x) + "," + F(actual.y) + "," +
                     F(actual.z) + ") euler=(" + F(euler.x) + "," + F(euler.y) + "," + F(euler.z) + ") fov=" +
                     F(CameraService.GetFieldOfView(camera)) +
                     " | 期望 pos=(" + F(_presetPose.x) + "," + F(_presetPose.y) + "," + F(_presetPose.z) +
                     ") pitch=" + F(_presetUsedPitch) + " fov=" + F(_presetUsedFov) +
                     " | 偏差=" + F(err) + "m" + (err < 0.5f ? " (写入生效)" : " (写入未生效!)"));

            if (err > 0.5f)
                Log.Warn("预设位姿没有写进相机: 相机可能拒绝外部写入(偏差 " + F(err) + "m)");
        }

        /// <summary>
        /// 帧首校验: 在 Update 时机(上一帧渲染已经结束)读相机位姿。
        ///   - 仍等于预设位姿 -> 上一帧渲染用的就是预设视角(接管真的生效了);
        ///   - 变成游戏自己的位姿 -> 游戏在我们的写入之后还有一次写入, 需要换接管时机。
        /// 这是本游戏里唯一能"不看画面"就判定视角是否生效的方法。
        /// </summary>
        private void LogFrameStartCheck()
        {
            var camera = (_driven != null && UnityObject.IsAlive(_driven)) ? _driven : null;
            if (camera == null) return;

            Vector3 actual = CameraService.GetPosition(camera);
            float err = Distance(actual, _presetPose);

            Log.Info("帧首校验(第" + _frameStartChecks + "帧): 相机=(" + F(actual.x) + "," + F(actual.y) + "," +
                     F(actual.z) + ") 预设=(" + F(_presetPose.x) + "," + F(_presetPose.y) + "," + F(_presetPose.z) +
                     ") 偏差=" + F(err) + "m" +
                     (err < 0.5f ? " -> 上一帧渲染用的就是预设视角(生效)"
                                 : " -> 游戏在我们的写入之后又改了相机(未生效)"));
        }

        private static float Distance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// 是否是"还没初始化"的占位相机(菜单/加载阶段)。
        /// 判据: 位置在原点且朝向为 (0,0,0) —— 游戏真正的游戏相机不会长这样。
        /// 只用于推迟 autoActivate(否则会拿占位相机算出一个没意义的瞄准点)。
        /// </summary>
        private static bool IsPlaceholderCamera(Camera camera)
        {
            Vector3 position = CameraService.GetPosition(camera);
            if (!position.IsZero()) return false;

            Vector3 euler = CameraService.GetEulerAngles(camera);
            return Math.Abs(NormalizeAngle(euler.x)) < 0.01f &&
                   Math.Abs(NormalizeAngle(euler.y)) < 0.01f;
        }

        // =====================================================================
        // 输入处理
        // =====================================================================

        private void HandleInput()
        {
            float dt = UnityTime.DeltaTime;
            if (dt <= 0f) dt = 1f / 60f;
            if (dt > 0.25f) dt = 0.25f; // 卡顿时限制单帧位移, 避免"瞬移"

            // ---- 鼠标转向(数学在 SDK 的 FreeCameraMath 内, 已被单元测试覆盖) ----
            Vector2 mouse = InputService.GetMouseDelta();
            if (mouse.sqrMagnitude > 0.000001f)
            {
                _zeroMouseFrames = 0;
                float newYaw, newPitch;
                if (IsOrbit)
                {
                    // 轨道模式: 上移 = 抬高相机并俯视(与飞行模式的"上移 = 抬头"相反)
                    FreeCameraMath.ApplyOrbitLook(_yaw, _pitch, mouse.x, mouse.y, _rotationSpeed, _invertY,
                        out newYaw, out newPitch);
                }
                else
                {
                    FreeCameraMath.ApplyMouseLook(_yaw, _pitch, mouse.x, mouse.y, _rotationSpeed, _invertY,
                        out newYaw, out newPitch);
                }
                _yaw = newYaw;
                _pitch = newPitch;
            }
            else if (++_zeroMouseFrames == 120 && _cursorCaptured)
            {
                // Unity 的 Mouse X/Y 轴不可用(可能输入模块未反射到): 解除锁定, 改用光标位移
                InputService.RestoreCursor();
                _cursorCaptured = false;
                Log.Warn("鼠标轴 120 帧无响应, 已解除光标锁定并改用光标位移");
                UiService.Notify("自由相机: 已切换鼠标模式", UiNotificationLevel.Warning, 2f, Context);
            }

            // ---- 滚轮: FOV; 轨道模式下 Alt+滚轮 = 推拉与观察点的距离 ----
            float scroll = InputService.GetMouseScroll();
            if (Math.Abs(scroll) > 0.0001f)
            {
                bool alt = InputService.IsKeyHeld(KeyCode.LeftAlt) || InputService.IsKeyHeld(KeyCode.RightAlt);
                if (IsOrbit && alt)
                {
                    _distance = FreeCameraMath.ApplyScrollToDistance(_distance, scroll, _distanceStep,
                        FreeCameraMath.MinOrbitDistance, FreeCameraMath.MaxOrbitDistance);
                }
                else
                {
                    _currentFov = FreeCameraMath.ApplyScrollToFieldOfView(_currentFov, scroll);
                }
            }

            // ---- 位移只在飞行模式生效: 轨道模式的相机位置由"观察点 + 球面"决定 ----
            if (!IsOrbit)
            {
                bool fast = InputService.IsKeyHeld(KeyCode.LeftShift) || InputService.IsKeyHeld(KeyCode.RightShift);
                bool slow = InputService.IsKeyHeld(KeyCode.LeftControl) || InputService.IsKeyHeld(KeyCode.RightControl);
                float speed = _moveSpeed * FreeCameraMath.SpeedMultiplier(fast, slow);

                float right = 0f, up = 0f, forward = 0f;
                if (InputService.IsKeyHeld(KeyCode.W) || InputService.IsKeyHeld(KeyCode.UpArrow)) forward += 1f;
                if (InputService.IsKeyHeld(KeyCode.S) || InputService.IsKeyHeld(KeyCode.DownArrow)) forward -= 1f;
                if (InputService.IsKeyHeld(KeyCode.D) || InputService.IsKeyHeld(KeyCode.RightArrow)) right += 1f;
                if (InputService.IsKeyHeld(KeyCode.A) || InputService.IsKeyHeld(KeyCode.LeftArrow)) right -= 1f;
                if (InputService.IsKeyHeld(KeyCode.E)) up += 1f;
                if (InputService.IsKeyHeld(KeyCode.Q)) up -= 1f;

                if (right != 0f || up != 0f || forward != 0f)
                {
                    float dx, dy, dz;
                    FreeCameraMath.ComposeDelta(_yaw, _pitch, forward, right, up, speed, dt, out dx, out dy, out dz);
                    if (dx != 0f || dy != 0f || dz != 0f)
                        _position += new Vector3(dx, dy, dz);
                }
            }

            Apply();
        }

        /// <summary>把当前位姿/参数写到"接管对象"上。</summary>
        private void Apply()
        {
            // 轨道模式: 相机位置由"观察点 + yaw/仰角/距离"决定, 朝向始终指向观察点
            // (仰角就是 pitch, 所以 Quaternion.Euler(pitch, yaw, 0) 正好直视观察点)
            Vector3 position = _position;
            if (IsOrbit)
            {
                float x, y, z;
                FreeCameraMath.OrbitPosition(_pivot.x, _pivot.y, _pivot.z, _yaw, _pitch, _distance,
                    out x, out y, out z);
                position = new Vector3(x, y, z);
            }

            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            // 模式 1: Cinemachine VirtualCamera
            if (_freeVcam != null)
            {
                var transform = UnityObject.GetTransform(_freeVcam as UnityEngine.Object);
                if (transform != null)
                {
                    TransformService.SetPosition(transform, position);
                    TransformService.SetRotation(transform, rotation);
                }
                CinemachineService.SetLensFieldOfView(_freeVcam, _currentFov);
                CinemachineService.SetLensNearClipPlane(_freeVcam, _nearClip);
                CinemachineService.SetLensFarClipPlane(_freeVcam, _farClip);
                return;
            }

            // 模式 2: 直接驱动相机
            var camera = (_driven != null && UnityObject.IsAlive(_driven)) ? _driven : CameraService.GetMainCamera();
            if (!UnityObject.IsAlive(camera))
            {
                Log.Warn("驱动目标相机丢失, 自动关闭自由相机");
                Deactivate("相机丢失");
                return;
            }
            _driven = camera;

            CameraService.SetPosition(camera, position);
            CameraService.SetRotation(camera, rotation);
            CameraService.SetFieldOfView(camera, _currentFov);
            CameraService.SetNearClipPlane(camera, _nearClip);
            CameraService.SetFarClipPlane(camera, _farClip);
        }

        /// <summary>F2: 回到进入时的位置/朝向/FOV。</summary>
        private void ResetToSaved()
        {
            try
            {
                if (!_savedState.Valid)
                {
                    Log.Warn("没有可用的初始状态, 无法复位");
                    return;
                }

                _position = _savedState.Position;
                _yaw = _savedState.EulerAngles.y;
                _pitch = NormalizeAngle(_savedState.EulerAngles.x);
                _currentFov = _savedState.FieldOfView > 0.1f ? _savedState.FieldOfView : _defaultFov;

                if (IsOrbit)
                {
                    // 观察点与距离也要复位, 否则只还原角度会让画面跳到别处
                    _pivot = _savedPivot;
                    _distance = _savedDistance;
                }

                Apply();

                Log.Info(IsOrbit ? "已复位到进入时的观察点/距离/角度/FOV" : "已复位到进入时的位置与朝向");
                UiService.Notify("自由相机: 已复位", UiNotificationLevel.Info, 1.5f, Context);
            }
            catch (Exception e)
            {
                Log.ReportCrash("ResetToSaved", e);
            }
        }

        // =====================================================================
        // 重新绑定
        // =====================================================================

        private void OnMainCameraLost()
        {
            if (_active) Log.Warn("主相机消失(将在下次 OnUpdate 重新绑定)");
        }

        private void OnMainCameraChanged(Camera camera)
        {
            if (_active) Rebind("主相机更换 -> " + (UnityObject.GetName(camera) ?? "?"));
        }

        /// <summary>
        /// 重新绑定到当前主相机: 丢弃旧的 VirtualCamera / 驱动引用, 用当前相机状态重新开始。
        /// 场景切换后旧相机已销毁, 所以这里不做"还原"(还原一个已销毁的对象没有意义)。
        /// </summary>
        private void Rebind(string reason)
        {
            Log.Info("自由相机重新绑定: " + reason);

            // 记住当前位姿, 重建后继续保持, 避免视角跳变
            Vector3 position = _position;
            Vector3 pivot = _pivot;
            float distance = _distance;
            float yaw = _yaw, pitch = _pitch, fov = _currentFov;

            _active = false;

            // 清理旧接管对象
            if (_freeVcam != null)
            {
                CinemachineService.DestroyVirtualCamera(_freeVcam);
                _freeVcam = null;
            }

            // 还原 Brain 的启用状态(按进入前的记录; preset 模式从不碰 Brain)
            if (!IsPreset && _savedBrain.Available && _savedBrain.BrainFound)
            {
                var oldBrain = CinemachineService.FindBrain();
                if (oldBrain != null) CinemachineService.SetBrainEnabled(oldBrain, _savedBrain.BrainEnabled);
            }

            _driven = null;

            var camera = CameraService.GetMainCamera();
            if (!UnityObject.IsAlive(camera))
            {
                Log.Warn("重新绑定时没有可用主相机, 自由相机保持关闭");
                if (_cursorCaptured)
                {
                    InputService.ReleaseMouse(Context);
                    _cursorCaptured = false;
                }
                _mode = "(未激活)";
                return;
            }

            // 以新相机为基准重新保存状态
            _savedState = CameraService.CaptureState(camera);
            _savedBrain = CinemachineService.CaptureBrainState();

            if (IsPreset)
            {
                _driven = camera;
                _mode = "直接驱动主相机(预设俯瞰, PostLateUpdate 写入)";
            }
            else if (_savedBrain.Available && _savedBrain.BrainFound)
            {
                _freeVcam = CinemachineService.CreateVirtualCamera("CesiumFreeCamera", null, 100);
                if (_freeVcam != null) _mode = "Cinemachine VirtualCamera(priority=100)";
            }

            if (_freeVcam == null && !IsPreset)
            {
                _driven = camera;
                _mode = "直接驱动主相机";
                if (_savedBrain.Available && _savedBrain.BrainFound)
                {
                    var brain = CinemachineService.FindBrain();
                    if (CinemachineService.SetBrainEnabled(brain, false))
                        _mode = "直接驱动主相机(已临时禁用 Cinemachine Brain)";
                }
            }

            _position = position.IsZero() ? CameraService.GetPosition(camera) : position;
            _yaw = yaw;
            _pitch = pitch;
            _currentFov = fov > 0.1f ? fov : _defaultFov;

            if (IsPreset)
            {
                // 新场景的新相机: 按配置重算预设位姿(位置/朝向都从新相机推)
                ComputePresetPose(camera);
            }
            else if (IsOrbit)
            {
                // 观察点是世界坐标, 跨场景仍然有效 -> 继续用, 避免视角跳变;
                // 还没建立过(理论上不会)则按新相机重建一个
                if (pivot.IsZero()) SetupOrbit();
                else { _pivot = pivot; _distance = distance; }
            }

            _active = true;
            if (IsPreset) ApplyPreset();
            else Apply();

            Log.Info("自由相机已重新绑定 [接管: " + _mode + " / 模式: " + ModeName() + "]" +
                     (IsPreset
                         ? " 观察点=(" + F(_presetTarget.x) + "," + F(_presetTarget.y) + "," + F(_presetTarget.z) +
                           ") 相机=(" + F(_presetPose.x) + "," + F(_presetPose.y) + "," + F(_presetPose.z) + ")"
                         : ""));
        }

        // =====================================================================
        // 配置
        // =====================================================================

        private void LoadConfig()
        {
            if (_config == null) return;

            _cameraMode = _config.GetString("mode", _cameraMode);
            _presetHeight = _config.GetFloat("height", _presetHeight);
            _presetPitch = _config.GetFloat("pitch", _presetPitch);
            _presetFov = _config.GetFloat("fov", _presetFov);   // preset 模式的固定 FOV(与 orbit/fly 的兜底 FOV 共用一个键)
            _boardHeight = _config.GetFloat("boardHeight", _boardHeight);
            _aimDistance = _config.GetFloat("aimDistance", _aimDistance);
            _maxTargetDistance = _config.GetFloat("maxTargetDistance", _maxTargetDistance);
            _autoActivate = _config.GetBool("autoActivate", _autoActivate);
            _autoActivateDelay = _config.GetFloat("autoActivateDelay", _autoActivateDelay);
            _orbitDistance = _config.GetFloat("orbitDistance", _orbitDistance);
            _distanceStep = _config.GetFloat("distanceStep", _distanceStep);
            _moveSpeed = _config.GetFloat("speed", _moveSpeed);
            _rotationSpeed = _config.GetFloat("rotationSpeed", _rotationSpeed);
            _defaultFov = _config.GetFloat("fov", _defaultFov);
            _nearClip = _config.GetFloat("nearClip", _nearClip);
            _farClip = _config.GetFloat("farClip", _farClip);
            _invertY = _config.GetBool("invertY", _invertY);
            _lockCursor = _config.GetBool("lockCursor", _lockCursor);

            _toggleKey = ParseKey(_config.GetString("toggleKey", "F1"), _toggleKey);
            _resetKey = ParseKey(_config.GetString("resetKey", "F2"), _resetKey);

            // 首次运行(或从旧版本升级, 缺 mode/height 等键)时把默认值写盘, 方便用户直接改
            if (!_config.Has("speed") || !_config.Has("mode") || !_config.Has("height"))
            {
                _config.Set("mode", _cameraMode);
                _config.Set("height", _presetHeight);
                _config.Set("pitch", _presetPitch);
                _config.Set("boardHeight", _boardHeight);
                _config.Set("aimDistance", _aimDistance);
                _config.Set("maxTargetDistance", _maxTargetDistance);
                _config.Set("autoActivate", _autoActivate);
                _config.Set("autoActivateDelay", _autoActivateDelay);
                _config.Set("orbitDistance", _orbitDistance);
                _config.Set("distanceStep", _distanceStep);
                _config.Set("speed", _moveSpeed);
                _config.Set("rotationSpeed", _rotationSpeed);
                _config.Set("fov", _defaultFov);
                _config.Set("nearClip", _nearClip);
                _config.Set("farClip", _farClip);
                _config.Set("invertY", _invertY);
                _config.Set("lockCursor", _lockCursor);
                _config.Set("toggleKey", _toggleKey.ToString());
                _config.Set("resetKey", _resetKey.ToString());
                if (_config.Save()) Log.Info("已生成默认配置: " + _config.Path);
            }
        }

        private void SaveConfig()
        {
            if (_config == null) return;
            _config.Set("mode", _cameraMode);
            _config.Set("height", _presetHeight);
            _config.Set("pitch", _presetPitch);
            _config.Set("boardHeight", _boardHeight);
            _config.Set("aimDistance", _aimDistance);
            _config.Set("maxTargetDistance", _maxTargetDistance);
            _config.Set("autoActivate", _autoActivate);
            _config.Set("autoActivateDelay", _autoActivateDelay);
            _config.Set("orbitDistance", _orbitDistance);
            _config.Set("distanceStep", _distanceStep);
            _config.Set("speed", _moveSpeed);
            _config.Set("rotationSpeed", _rotationSpeed);
            _config.Set("fov", _defaultFov);
            _config.Set("nearClip", _nearClip);
            _config.Set("farClip", _farClip);
            _config.Set("invertY", _invertY);
            _config.Set("lockCursor", _lockCursor);
            _config.Set("toggleKey", _toggleKey.ToString());
            _config.Set("resetKey", _resetKey.ToString());
            _config.SaveIfDirty();
        }

        private static KeyCode ParseKey(string name, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            try
            {
                KeyCode parsed;
                return Enum.TryParse(name, true, out parsed) ? parsed : fallback;
            }
            catch { return fallback; }
        }

        private static float NormalizeAngle(float angle)
        {
            return FreeCameraMath.NormalizeAngle180(angle);
        }

        private static string F(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    internal static class VectorExtensions
    {
        /// <summary>是否为零向量(容差比较)。</summary>
        internal static bool IsZero(this Vector3 v)
        {
            return Math.Abs(v.x) < 0.0001f && Math.Abs(v.y) < 0.0001f && Math.Abs(v.z) < 0.0001f;
        }
    }
}
