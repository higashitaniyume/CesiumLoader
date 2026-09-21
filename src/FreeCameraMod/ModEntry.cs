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
    ///   mode / height / heightStep / pitch / fov / boardHeight / aimDistance /
    ///   maxTargetDistance / maxReadyCameraHeight / autoActivate / autoActivateDelay /
    ///   orbitDistance / distanceStep / speed / rotationSpeed /
    ///   nearClip / farClip / invertY / toggleKey / resetKey / lockCursor
    ///
    /// 热键(俯瞰类模式 preset/raise 通用):
    ///   toggleKey(默认 F1)  开关;
    ///   Ctrl+= / 小键盘 +   抬高 height, 每按一次 heightStep 米;
    ///   Ctrl+- / 小键盘 -   降低 height, 每按一次 heightStep 米。
    /// 高度热键只"读"键盘, 不抢鼠标/其它按键; 改完直接在下一帧生效(不必重启), 并写回 config.json。
    /// </summary>
    public sealed class FreeCameraController : ModBase
    {
        // ---------------- 配置 ----------------
        private ModConfig _config;
        private string _cameraMode = "raise";   // "raise" 跟随抬高(默认, 保留游戏自己的视角操作) / "preset" 固定俯瞰 / "orbit" 环绕 / "fly" 飞行
        private float _presetHeight = 160f;     // preset: 相机绝对高度(Y)
        private float _presetPitch = 70f;       // preset: 俯角(度, >0 向下看)
        private float _presetFov = 80f;         // preset: 固定 FOV(越大视野越宽)
        private float _boardHeight;             // preset: 棋盘/桌面高度(Y), 默认 0 = 地面
        private float _aimDistance;             // preset: >0 = 手动指定瞄准距离; 0 = 自动(沿用游戏原本的瞄准点)
        private float _maxTargetDistance = 500f;// preset/raise: 自动瞄准的交点距离上限(防止相机接近水平时算出天量距离)
        private float _maxReadyCameraHeight = 500f; // preset/raise: 高于此高度且朝向为0的相机视为"菜单/过渡相机", 拒绝接管
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
        private float _heightStep = FreeCameraMath.DefaultHeightStep; // Ctrl+= / Ctrl+- 每次调整的高度(米)

        // ---------------- 高度热键的写盘状态 ----------------
        // 按住 Ctrl 连按 "+"/"-" 时不要每次都写盘: 攒到停止按键约 1 秒后再落盘(见 HandleHeightHotkeys)。
        private const float HeightSaveDelaySec = 1.2f;
        private bool _heightDirty;              // 高度被热键改过, 待写回 config.json
        private float _heightSaveAt;            // 可以落盘的时刻(RealtimeSinceStartup, 不受变速/暂停影响)

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
        private bool _presetAimClamped;          // 本次算出的瞄准距离被 maxTargetDistance 截断(相机没在看棋盘)

        // ---------------- 跟随抬高模式(raise)运行态 ----------------
        // 思路: 每帧先读"游戏自己写下的位姿"当基准(而不是把相机钉死), 再按 height/pitch/fov
        // 把它抬到高处; 水平朝向与瞄准点仍跟着游戏走, 于是游戏自己的鼠标转视角/平移照常生效。
        private Vector3 _basePosition;           // 识别出的"游戏自己的"相机位姿
        private Vector3 _baseEuler;
        private bool _hasBasePose;
        private Vector3 _lastWrittenPosition;    // 上一帧我们写下的值(用于识别"游戏这帧有没有写相机")
        private float _lastWrittenPitch;
        private float _lastWrittenYaw;
        private bool _hasWrittenPose;
        private int _raiseFrames;                // 本次接管的写入帧数(用于跟随校验日志)

        /// <summary>是否环绕观察模式。</summary>
        private bool IsOrbit
        {
            get { return string.Equals(_cameraMode, "orbit", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// 是否预设俯瞰模式: 相机每帧被钉死在配置好的位姿上。
        ///
        /// ⚠️ 这个模式<b>会顶掉游戏自己的视角操作</b>(鼠标转视角/平移一律看不出效果),
        /// 因为游戏每帧写下的相机位姿都会被我们覆盖 —— 实测就是"F1 打开后动不了, 关掉就恢复"。
        /// 想边抬高视角边继续正常操作游戏, 用 <see cref="IsRaise"/>(默认模式)。
        /// </summary>
        private bool IsPreset
        {
            get { return string.Equals(_cameraMode, "preset", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// 是否跟随抬高模式(默认, mode=raise/follow/offset):
        /// 保留游戏自己的视角控制, 只把相机抬到 height、按 pitch 俯视、用 fov 的视野。
        /// 每帧以"游戏写下的位姿"为基准重新计算, 所以游戏里用鼠标转视角/平移地图都还有效,
        /// 只是始终从高处往下看。
        /// </summary>
        private bool IsRaise
        {
            get
            {
                return string.Equals(_cameraMode, "raise", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(_cameraMode, "follow", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(_cameraMode, "offset", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>是否"俯瞰类"模式(preset/raise 共用 height/pitch/fov 这组配置)。</summary>
        private bool IsOverhead
        {
            get { return IsPreset || IsRaise; }
        }

        /// <summary>显示名。</summary>
        public override string Name { get { return "自由相机"; } }

        /// <summary>版本。</summary>
        public override string Version { get { return "2.1.5"; } }

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
                     (IsOverhead
                         ? " height=" + F(_presetHeight) + " pitch=" + F(_presetPitch) +
                           " fov=" + F(_presetFov) + " boardHeight=" + F(_boardHeight) +
                           " aimDistance=" + F(_aimDistance)
                         : IsOrbit
                             ? " orbitDistance=" + F(_orbitDistance) + " distanceStep=" + F(_distanceStep)
                             : " speed=" + F(_moveSpeed)) +
                     " rotationSpeed=" + F(_rotationSpeed) +
                     " fov=" + F(_defaultFov) + " lockCursor=" + _lockCursor + ")");
            if (IsRaise)
            {
                Log.Info("操作: " + _toggleKey + " 开关; Ctrl+= (或小键盘 +) 抬高相机, Ctrl+- 降低" +
                         " —— 每按一次 " + F(_heightStep) + "m, 立刻生效并写回配置. " +
                         "视角会自动抬高, 游戏自己的鼠标/键盘操作照常可用(本 mod 只改相机位姿, 不接管鼠标)");
                Log.Info("跟随抬高: 相机高度 Y=" + F(_presetHeight) + ", 俯角 " + F(_presetPitch) +
                         "°, FOV " + F(_presetFov) + ", 棋盘高度 Y=" + F(_boardHeight) +
                         (_aimDistance > 0.01f ? ", 手动瞄准距离 " + F(_aimDistance) + "m"
                                               : ", 瞄准点=自动沿用游戏当前的瞄准点") +
                         (_autoActivate ? ", 将在 " + F(_autoActivateDelay) + "s 后自动进入" : ""));
            }
            else if (IsPreset)
            {
                Log.Info("操作: " + _toggleKey + " 开关; Ctrl+= (或小键盘 +) 抬高相机, Ctrl+- 降低" +
                         " —— 每按一次 " + F(_heightStep) + "m(其它按键/鼠标/滚轮均不接管)");
                Log.Info("预设视角: 相机高度 Y=" + F(_presetHeight) + ", 俯角 " + F(_presetPitch) +
                         "°, FOV " + F(_presetFov) + ", 棋盘高度 Y=" + F(_boardHeight) +
                         (_aimDistance > 0.01f ? ", 手动瞄准距离 " + F(_aimDistance) + "m"
                                               : ", 棋盘中心=自动沿用游戏原本的瞄准点") +
                         (_autoActivate ? ", 将在 " + F(_autoActivateDelay) + "s 后自动进入" : ""));
                Log.Warn("注意: preset 是完全固定视角 —— 相机每帧被写死, 游戏自己的鼠标/键盘视角操作会没有反应" +
                         "(实测: 开着就动不了, 关掉才恢复). 需要边看高处边操作游戏请把 mode 改成 raise");
            }
            else if (IsOrbit)
                Log.Info("操作: 鼠标=绕观察点转圈/上移抬高相机, 滚轮=FOV, Alt+滚轮=推拉距离, " +
                         _resetKey + "=复位");
            else
                Log.Info("操作: WASD=移动, Q/E=升降, 鼠标=转向, Shift=加速, Ctrl=减速, 滚轮=FOV, " +
                         _resetKey + "=复位");
            Log.Info("配置键: mode(raise/preset/orbit/fly)/height/heightStep/pitch/fov/boardHeight/aimDistance/" +
                     "maxTargetDistance/maxReadyCameraHeight/autoActivate/autoActivateDelay/orbitDistance/" +
                     "distanceStep/speed/rotationSpeed/nearClip/farClip/invertY/toggleKey/resetKey/lockCursor");
        }

        /// <summary>模式显示名。</summary>
        private string ModeName()
        {
            if (IsRaise) return "跟随抬高 raise";
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
                // 自动进入俯瞰视角(配置 autoActivate; 只在相机就绪后触发一次, 用户按过开关键后不再自动触发)
                if (!_active && !_autoActivateDone && _autoActivate && IsOverhead)
                {
                    _autoActivateFrames++;
                    if (_autoActivateFrames % 30 == 1)
                    {
                        int elapsedMs = unchecked(Environment.TickCount - _initTick);
                        if (elapsedMs >= (int)(_autoActivateDelay * 1000f))
                        {
                            var cam = CameraService.GetMainCamera();
                            string notReady = null;
                            if (!UnityObject.IsAlive(cam) || !TryPrepareOverhead(cam, out notReady))
                            {
                                // 菜单/加载阶段的相机是占位相机或失活的过渡相机: 现在接管只会算出一个
                                // 没意义的瞄准点, 等真正的游戏相机出现再进(进棋盘后主相机会被替换)。
                                if (!_autoActivateWarned)
                                {
                                    _autoActivateWarned = true;
                                    Log.Info("自动进入推迟: 主相机尚未就绪(" +
                                             (notReady ?? "没有主相机") + "), 等进入棋盘后再接管");
                                }
                            }
                            else
                            {
                                _autoActivateDone = true;
                                Log.Info("自动进入" + (IsRaise ? "跟随抬高视角" : "俯瞰视角") +
                                         " (autoActivate=true, 延迟 " + F(_autoActivateDelay) +
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

                // 俯瞰类模式(preset/raise): 不绑定其它按键, 相机由 OnLateUpdate 每帧保持。
                // 只额外接管 Ctrl+= / Ctrl+- 两个组合键(实时调高度, 不碰鼠标)。
                // preset 顺带做一次"帧首校验", 用来判定上一帧渲染是否真的用了预设视角;
                // raise 下游戏每帧本来就会写相机, 帧首位姿就是它的基准, 不做"是否生效"的判定。
                if (IsOverhead)
                {
                    HandleHeightHotkeys();

                    if (IsPreset)
                    {
                        _frameStartChecks++;
                        if (_frameStartChecks == 60 || _frameStartChecks == 300) LogFrameStartCheck();
                    }
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
        /// preset/raise 模式的接管写在这里: 游戏的相机逻辑(自己的 LateUpdate 或 Cinemachine Brain)
        /// 跑完之后我们再写一遍, 所以渲染出来的必定是我们要的视角 —— 这是本游戏里唯一
        /// 可靠的接管点(实测 Brain 在失活对象上, 单靠 VirtualCamera 不会生效)。
        /// 只写相机姿态与镜头参数, 不碰游戏逻辑, 退出时按保存的 CameraState 还原
        /// (注意: 不还原 enabled, 见 CameraState.Restore 的说明 —— 还原它会把画面弄黑)。
        /// </summary>
        public override void OnLateUpdate()
        {
            if (!_active || !IsOverhead) return;
            try
            {
                if (IsPreset) ApplyPreset();
                else ApplyRaise();
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

            // 0) 就绪校验(仅俯瞰类): 实测菜单/切场景时主相机是失活的过渡相机(停在 Y≈1002.75,
            //    euler=(0,0,0)), 用它算出的"瞄准点"毫无意义; 一旦接管, 退出时还会把这份坏状态
            //    还原回相机 -> 画面变黑(HUD 仍在)。所以这里一律拒绝, 等就绪再进。
            if (IsOverhead)
            {
                string notReady;
                if (!TryPrepareOverhead(camera, out notReady))
                {
                    Log.Warn("暂不接管: " + notReady + " (等进入棋盘/加载完成后再按 " + _toggleKey + ")");
                    UiService.Notify("自由相机: 相机未就绪 (" + notReady + ")",
                        UiNotificationLevel.Warning, 3f, Context);
                    return;
                }
            }

            // 1) 保存原始状态(相机 + Cinemachine)
            _savedState = CameraService.CaptureState(camera);
            _savedBrain = CinemachineService.CaptureBrainState();

            _freeVcam = null;
            _driven = null;

            // 2) 俯瞰类(preset/raise): 直接驱动主相机。本游戏 Cinemachine Brain 位于失活对象,
            //    VirtualCamera 不会驱动任何相机, 所以这条路是唯一可靠的。
            if (IsOverhead)
            {
                _driven = camera;
                _mode = IsPreset ? "直接驱动主相机(预设俯瞰, PostLateUpdate 写入)"
                                 : "直接驱动主相机(跟随抬高, PostLateUpdate 写入)";
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
            if (_freeVcam == null && !IsOverhead)
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

            // 4b) 姿态: 俯瞰类的位姿已在 TryPrepareOverhead 里算好(立刻应用一次); orbit 建立观察点
            if (IsOverhead)
            {
                ResetRaiseState();
                // 记住"进入时的基准位姿": preset 的位姿只在这里算一次, 之后按 Ctrl+/- 调高度要靠它重算
                // (raise 每帧都会用游戏写下的位姿刷新基准, 这里写进去只是让 preset 也有基准可用)
                _basePosition = _position;
                _baseEuler = euler;
                _hasBasePose = true;

                if (IsPreset) ApplyPreset();
                else ApplyRaise();
            }
            else
            {
                SetupOrbit();
            }

            // 5) 鼠标独占(仅 orbit/fly 需要; preset/raise 完全不接管鼠标, 游戏自己的操作照常)
            if (IsOverhead)
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
                     (IsOverhead
                         ? " 瞄准点=(" + F(_presetTarget.x) + "," + F(_presetTarget.y) + "," + F(_presetTarget.z) +
                           ") 相机=(" + F(_presetPose.x) + "," + F(_presetPose.y) + "," + F(_presetPose.z) +
                           ") 俯角=" + F(_presetUsedPitch) + " FOV=" + F(_presetUsedFov) +
                           (string.IsNullOrEmpty(_presetNote) ? "" : " [" + _presetNote + "]")
                         : IsOrbit
                             ? " 观察点=(" + F(_pivot.x) + "," + F(_pivot.y) + "," + F(_pivot.z) +
                               ") 距离=" + F(_distance) + " 仰角=" + F(_pitch)
                             : "") +
                     " 进入前状态: " + _savedState.Describe());
            UiService.Notify(IsRaise ? "自由相机: 跟随抬高已开启 (" + _toggleKey + " 关闭)"
                                     : IsPreset ? "自由相机: 俯瞰视角已开启 (" + _toggleKey + " 关闭)"
                                                : "自由相机: 开启 (" + _toggleKey + " 关闭)",
                UiNotificationLevel.Info, 2.5f, Context);
        }

        /// <summary>退出自由相机并还原。</summary>
        public void Deactivate(string reason)
        {
            if (!_active) return;
            _active = false;

            // 1) 还原 Cinemachine(同时销毁我们创建的 VirtualCamera)
            //    俯瞰类(preset/raise)从不碰 Brain/VirtualCamera(只驱动相机本体), 所以跳过, 避免
            //    去"还原"一个我们从未改过的 Brain。
            try
            {
                if (IsOverhead)
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

            // 2) 还原主相机状态(位姿/镜头参数)
            //    注意: 不还原 enabled —— 见 CameraState.Restore 的说明, 快照可能采自过渡期的
            //    失活相机, 把 enabled=false 还原回去会让画面整个变黑(HUD 还在, 声音照旧)。
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
            ResetRaiseState();
            FlushHeightSaveNow();   // 用热键调过的高度别丢(写盘有延迟, 这里补一次)

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
        /// 俯瞰几何: 由"基准位姿"算出相机应该待的位置(preset 与 raise 共用同一套数学)。
        ///
        ///   瞄准点           = 基准相机正在看的那一点: 朝向射线与高度为 <c>boardHeight</c>
        ///                      平面的交点(这样只是"升高 + 拉远 + 视野变广", 视线中心不变)。
        ///                      <c>aimDistance</c> &gt; 0 时改为手动指定水平距离。
        ///   相机高度         = <c>height</c>(绝对 Y)
        ///   水平后退距离     = (height - 瞄准点Y) / tan(pitch)  → 保证相机正对瞄准点
        ///   朝向             = Euler(pitch, 基准 yaw, 0), 镜头 FOV = <c>fov</c>
        ///
        /// 于是 height / pitch / fov 三个数字就完全确定视角: 相机永远正对瞄准点。
        /// preset 的"基准"取进入时的相机(位姿固定); raise 的"基准"每帧换成游戏当前写下的
        /// 相机位姿(位姿跟着游戏走, 所以游戏自己的视角操作仍然有效)。
        ///
        /// 只写 _yaw/_presetTarget/_presetPose/_presetUsedPitch/_presetUsedFov/_presetNote/
        /// _presetAimClamped, 不动任何计数(调用方按需重置)。
        /// </summary>
        private void ApplyOverheadGeometry(Vector3 basePosition, Vector3 baseEuler)
        {
            // 水平朝向沿用基准的朝向(不做水平旋转); 俯角用配置值并钳制到合法范围
            _yaw = baseEuler.y;
            float entryPitch = NormalizeAngle(baseEuler.x);
            _presetUsedPitch = FreeCameraMath.Clamp(_presetPitch, 5f, 89f);
            _presetUsedFov = _presetFov > 1f ? _presetFov : _defaultFov;
            if (_presetUsedFov < 5f) _presetUsedFov = 5f;
            if (_presetUsedFov > 170f) _presetUsedFov = 170f;

            float yawRad = _yaw * (float)Math.PI / 180f;
            float hx = (float)Math.Sin(yawRad);   // 水平前方向(与 ForwardVector 同一约定)
            float hz = (float)Math.Cos(yawRad);

            _presetNote = "";
            _presetAimClamped = false;

            // ---- 瞄准点 ----
            float tx, tz;
            if (_aimDistance > 0.01f)
            {
                tx = basePosition.x + hx * _aimDistance;
                tz = basePosition.z + hz * _aimDistance;
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

                float t = (basePosition.y - _boardHeight) / down;
                if (t < 1f) t = 1f;
                if (t > _maxTargetDistance)
                {
                    t = _maxTargetDistance;
                    _presetAimClamped = true;     // 相机没在看棋盘(菜单/过渡相机会这样) -> 拒绝接管
                    _presetNote = "瞄准距离超过上限 " + F(_maxTargetDistance);
                }
                tx = basePosition.x + fx * t;
                tz = basePosition.z + fz * t;
            }

            _presetTarget = new Vector3(tx, _boardHeight, tz);

            // ---- 相机位姿: 高度固定, 水平后退 dy/tan(pitch), 正好俯视瞄准点 ----
            float dy = _presetHeight - _presetTarget.y;
            float tan = (float)Math.Tan(_presetUsedPitch * Math.PI / 180.0);
            float back = tan > 0.0001f ? dy / tan : 0f;
            if (back < 1f) back = 1f;   // 高度不足时至少退开 1 米, 免得相机陷进瞄准点

            _presetPose = new Vector3(tx - hx * back, _presetHeight, tz - hz * back);

            if (_presetNote.Length > 0) _presetNote += "; ";
            _presetNote += "相机在瞄准点后方 " + F(back) + "m";
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
        /// 跟随抬高模式(raise)的每帧写入 —— "视角抬高但仍能正常操作游戏"的关键。
        ///
        /// preset 把相机钉死, 于是游戏自己的视角操作全部失效(实测: 开着动不了, 关掉就恢复);
        /// raise 改为:
        ///   1) 先读"游戏这帧写下的位姿"当基准。要排除我们自己上一帧写进去的值: 若当前位姿
        ///      与我们上次写入的完全一致, 说明游戏这帧没动相机(静态/暂停), 继续沿用上次
        ///      识别出的基准 —— 否则每帧都会在"抬高后的位置"上再加一次偏移, 越飞越高。
        ///   2) 用与 preset 完全相同的几何, 把基准位姿抬到 height、按 pitch 俯视、套用 fov;
        ///   3) 写回。渲染用的是抬高后的视角, 但水平朝向与瞄准点都跟着游戏走, 所以在游戏里
        ///      用鼠标转视角、平移地图依旧有效。
        /// </summary>
        private void ApplyRaise()
        {
            var camera = (_driven != null && UnityObject.IsAlive(_driven)) ? _driven : CameraService.GetMainCamera();
            if (!UnityObject.IsAlive(camera))
            {
                // 相机没了: 由 OnUpdate 的 Rebind 负责重新绑定, 这里静默(否则每帧刷日志)
                return;
            }
            _driven = camera;

            Vector3 gamePosition = CameraService.GetPosition(camera);
            Vector3 gameEuler = CameraService.GetEulerAngles(camera);

            // 游戏这帧有没有写相机? 位姿与我们上一帧写入的一致 -> 没有, 沿用上次的基准
            bool unchangedByGame = _hasWrittenPose && _hasBasePose &&
                                   Distance(gamePosition, _lastWrittenPosition) < 0.005f &&
                                   Math.Abs(NormalizeAngle(gameEuler.x - _lastWrittenPitch)) < 0.05f &&
                                   Math.Abs(NormalizeAngle(gameEuler.y - _lastWrittenYaw)) < 0.05f;

            if (!unchangedByGame)
            {
                _basePosition = gamePosition;
                _baseEuler = gameEuler;
                _hasBasePose = true;
            }

            // 以"游戏自己的位姿"为基准, 算抬高后的位姿
            ApplyOverheadGeometry(_basePosition, _baseEuler);

            CameraService.SetPosition(camera, _presetPose);
            CameraService.SetRotation(camera, Quaternion.Euler(_presetUsedPitch, _yaw, 0f));
            CameraService.SetFieldOfView(camera, _presetUsedFov);
            CameraService.SetNearClipPlane(camera, _nearClip);
            CameraService.SetFarClipPlane(camera, _farClip);

            _lastWrittenPosition = _presetPose;
            _lastWrittenPitch = _presetUsedPitch;
            _lastWrittenYaw = _yaw;
            _hasWrittenPose = true;

            _raiseFrames++;
            if (_raiseFrames == 60 || _raiseFrames == 300) LogRaiseCheck(gamePosition, gameEuler);
        }

        /// <summary>
        /// 俯瞰类模式(preset/raise)接管前的就绪校验, 顺便把位姿算出来(只算不写)。
        ///
        /// 实测踩坑: 本游戏菜单/切场景时主相机会失活并停在 Y≈1002.75、euler=(0,0,0) ——
        /// 此时算出的"瞄准点"会直接顶到 maxTargetDistance 上限, 完全没有意义; 若照此接管,
        /// 退出时还会把这份坏状态还原回相机(画面变黑而 HUD 还在)。所以这里一律拒绝,
        /// 等游戏真正进入棋盘再接管。
        /// </summary>
        private bool TryPrepareOverhead(Camera camera, out string reason)
        {
            reason = null;
            if (!UnityObject.IsAlive(camera)) { reason = "没有主相机"; return false; }

            Vector3 position = CameraService.GetPosition(camera);
            Vector3 euler = CameraService.GetEulerAngles(camera);
            if (IsPlaceholderCamera(position, euler))
            {
                reason = "主相机还是菜单/过渡相机(尚未进入棋盘)";
                return false;
            }

            ApplyOverheadGeometry(position, euler);
            _presetFrames = 0;
            _frameStartChecks = 0;

            if (_presetAimClamped)
            {
                reason = "相机没有看向棋盘(瞄准距离超过 " + F(_maxTargetDistance) +
                         "m); 稍后再按, 或调大 maxTargetDistance";
                return false;
            }

            if (!CameraService.GetEnabled(camera))
                Log.Warn("主相机当前是失活状态(enabled=false), 仍按当前位姿接管; 若画面异常请按 " +
                         _toggleKey + " 关闭");

            return true;
        }

        /// <summary>清空跟随抬高的基准位姿(进入/退出/重新绑定时调用, 强制下一帧重新识别)。</summary>
        private void ResetRaiseState()
        {
            _hasBasePose = false;
            _hasWrittenPose = false;
            _raiseFrames = 0;
        }

        /// <summary>
        /// 跟随抬高校验: 确认基准位姿来自游戏、且我们的写入确实生效。
        /// 偏差 ≈ 0 = 本帧渲染用的就是抬高后的视角; 基准位姿每帧都在变 = 游戏自己的操作有效。
        /// </summary>
        private void LogRaiseCheck(Vector3 gamePosition, Vector3 gameEuler)
        {
            var camera = (_driven != null && UnityObject.IsAlive(_driven)) ? _driven : null;
            if (camera == null) return;

            Vector3 actual = CameraService.GetPosition(camera);
            float err = Distance(actual, _presetPose);

            Log.Info("跟随校验(第" + _raiseFrames + "帧): 游戏位姿=(" + F(gamePosition.x) + "," + F(gamePosition.y) +
                     "," + F(gamePosition.z) + ") euler=(" + F(gameEuler.x) + "," + F(gameEuler.y) + "," +
                     F(gameEuler.z) + ") -> 抬高后=(" + F(_presetPose.x) + "," + F(_presetPose.y) + "," +
                     F(_presetPose.z) + ") 俯角=" + F(_presetUsedPitch) + " FOV=" + F(_presetUsedFov) +
                     " | 写入后实际=(" + F(actual.x) + "," + F(actual.y) + "," + F(actual.z) + ") 偏差=" +
                     F(err) + "m" + (err < 0.5f ? " (写入生效)" : " (写入未生效!)"));
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
        /// 是否是"还没就绪"的占位/过渡相机(菜单/加载阶段)。
        /// 判据: 朝向为 (0,0,0) 且 (位置在原点 或 高度超过 maxReadyCameraHeight)。
        /// 实测: 菜单/过渡相机是 euler=(0,0,0)、Y≈1002.75; 游戏真正的棋盘相机是
        /// euler=(45,315,0)、Y≈108 —— 这个判据能可靠区分两者。
        /// 只用于接管前的就绪校验(否则会拿占位相机算出一个没意义的瞄准点)。
        /// </summary>
        private bool IsPlaceholderCamera(Vector3 position, Vector3 euler)
        {
            if (Math.Abs(NormalizeAngle(euler.x)) >= 0.01f) return false;
            if (Math.Abs(NormalizeAngle(euler.y)) >= 0.01f) return false;
            return position.IsZero() || position.y > _maxReadyCameraHeight;
        }

        // =====================================================================
        // 游戏内实时调高度(Ctrl + "="/"-")
        // =====================================================================

        /// <summary>
        /// Ctrl + "="(也就是 "+", 小键盘 + 也行)= 抬高相机, Ctrl + "-" = 降低, 每次 <c>heightStep</c> 米。
        ///
        /// 为什么这样接:
        ///   - 只"读"键盘, 不接管鼠标、不拦截任何按键 —— 不会重演"mod 抢走视角操作"的问题;
        ///   - raise 每帧都会按 height 重算位姿, 所以改完下一帧就生效, 不必重启游戏;
        ///   - preset 的位姿是进入时算好的, 这里用记下的基准位姿重算一次(_presetPose);
        ///   - 连按时不逐次写盘, 停手约 <see cref="HeightSaveDelaySec"/> 秒后统一写回 config.json。
        /// </summary>
        private void HandleHeightHotkeys()
        {
            bool ctrl = InputService.IsKeyHeld(KeyCode.LeftControl) || InputService.IsKeyHeld(KeyCode.RightControl);
            if (!ctrl)
            {
                FlushHeightSaveIfDue();
                return;
            }

            // "=" 键本身就是 "+"(Shift+=), 小键盘另有独立的 +; "-" 同理。
            bool up = InputService.IsKeyPressed(KeyCode.Equals) || InputService.IsKeyPressed(KeyCode.KeypadPlus);
            bool down = InputService.IsKeyPressed(KeyCode.Minus) || InputService.IsKeyPressed(KeyCode.KeypadMinus);

            // 都没按(或两个同时按: 直接忽略, 免得方向反复横跳)
            if (up == down)
            {
                FlushHeightSaveIfDue();
                return;
            }

            float before = _presetHeight;
            _presetHeight = FreeCameraMath.ApplyHeightStep(_presetHeight, up ? 1f : -1f, _heightStep);

            if (Math.Abs(_presetHeight - before) < 0.001f)
            {
                // 已经顶到上下限: 只提示, 不写盘(否则每次按键都要碰一次磁盘)
                UiService.Notify("自由相机: 高度已到极限 " + F(_presetHeight) + "m",
                    UiNotificationLevel.Info, 1.5f, Context);
                FlushHeightSaveIfDue();
                return;
            }

            // preset 的位姿只在进入时算过一次, 这里按新的高度重算(raise 会在 OnLateUpdate 自己重算)
            if (IsPreset && _hasBasePose) ApplyOverheadGeometry(_basePosition, _baseEuler);

            _heightDirty = true;
            _heightSaveAt = UnityTime.RealtimeSinceStartup + HeightSaveDelaySec;

            Log.Info("高度调整: " + F(before) + "m -> " + F(_presetHeight) + "m (步进 " + F(_heightStep) + "m)");
            UiService.Notify("自由相机: 高度 " + F(_presetHeight) + "m", UiNotificationLevel.Info, 1.5f, Context);
        }

        /// <summary>到点了就把待写的高度落盘(连按期间只在停手后写一次)。</summary>
        private void FlushHeightSaveIfDue()
        {
            if (!_heightDirty) return;
            if (UnityTime.RealtimeSinceStartup < _heightSaveAt) return;
            FlushHeightSaveNow();
        }

        /// <summary>立刻把待写的高度落盘(退出接管 / 卸载时调用, 保证不丢)。</summary>
        private void FlushHeightSaveNow()
        {
            if (!_heightDirty) return;
            _heightDirty = false;
            try
            {
                SaveConfig();
                Log.Info("高度 " + F(_presetHeight) + "m 已写回配置" +
                         (_config != null && !string.IsNullOrEmpty(_config.Path) ? ": " + _config.Path : ""));
            }
            catch (Exception e) { Log.ReportCrash("保存高度", e); }
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

            // 还原 Brain 的启用状态(按进入前的记录; 俯瞰类模式从不碰 Brain)
            if (!IsOverhead && _savedBrain.Available && _savedBrain.BrainFound)
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

            if (IsOverhead)
            {
                _driven = camera;
                _mode = IsPreset ? "直接驱动主相机(预设俯瞰, PostLateUpdate 写入)"
                                 : "直接驱动主相机(跟随抬高, PostLateUpdate 写入)";
            }
            else if (_savedBrain.Available && _savedBrain.BrainFound)
            {
                _freeVcam = CinemachineService.CreateVirtualCamera("CesiumFreeCamera", null, 100);
                if (_freeVcam != null) _mode = "Cinemachine VirtualCamera(priority=100)";
            }

            if (_freeVcam == null && !IsOverhead)
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

            if (IsOverhead)
            {
                // 新场景的新相机: 重新做一次就绪校验并按配置重算位姿
                // (只算不写; 若新相机还没就绪就保留上一份位姿继续驱动, 避免把视角甩到过渡相机的方向)
                ResetRaiseState();
                string notReady;
                if (TryPrepareOverhead(camera, out notReady))
                {
                    // 就绪: 把新相机位姿当作基准记下来(preset 之后调高度要在它上面重算)
                    _basePosition = CameraService.GetPosition(camera);
                    _baseEuler = CameraService.GetEulerAngles(camera);
                    _hasBasePose = true;
                }
                else
                    Log.Warn("重新绑定时相机尚未就绪(" + notReady + "), 暂用上一份视角继续; 若画面异常请按 " +
                             _toggleKey + " 关闭");
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
            else if (IsRaise) ApplyRaise();
            else Apply();

            Log.Info("自由相机已重新绑定 [接管: " + _mode + " / 模式: " + ModeName() + "]" +
                     (IsOverhead
                         ? " 瞄准点=(" + F(_presetTarget.x) + "," + F(_presetTarget.y) + "," + F(_presetTarget.z) +
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
            _heightStep = _config.GetFloat("heightStep", _heightStep);
            _presetPitch = _config.GetFloat("pitch", _presetPitch);
            _presetFov = _config.GetFloat("fov", _presetFov);   // preset 模式的固定 FOV(与 orbit/fly 的兜底 FOV 共用一个键)
            _boardHeight = _config.GetFloat("boardHeight", _boardHeight);
            _aimDistance = _config.GetFloat("aimDistance", _aimDistance);
            _maxTargetDistance = _config.GetFloat("maxTargetDistance", _maxTargetDistance);
            _maxReadyCameraHeight = _config.GetFloat("maxReadyCameraHeight", _maxReadyCameraHeight);
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
                _config.Set("heightStep", _heightStep);
                _config.Set("pitch", _presetPitch);
                _config.Set("boardHeight", _boardHeight);
                _config.Set("aimDistance", _aimDistance);
                _config.Set("maxTargetDistance", _maxTargetDistance);
                _config.Set("maxReadyCameraHeight", _maxReadyCameraHeight);
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
            _config.Set("heightStep", _heightStep);
            _config.Set("pitch", _presetPitch);
            _config.Set("boardHeight", _boardHeight);
            _config.Set("aimDistance", _aimDistance);
            _config.Set("maxTargetDistance", _maxTargetDistance);
            _config.Set("maxReadyCameraHeight", _maxReadyCameraHeight);
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
