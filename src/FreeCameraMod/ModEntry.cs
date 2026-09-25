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
            ModBase.Run(new ZoomCameraController());
        }
    }

    /// <summary>
    /// 滚轮缩放(v2.2.1 —— 旧版"F1 开关俯瞰视角"已整体重写)。
    ///
    /// 要的行为:
    ///   1) 进游戏后<b>什么都不做</b>: 画面就是游戏自己的默认视角, 没有开关、没有自动接管;
    ///   2) <b>滚轮 = 放大/缩小, 且保持原来的视角</b>: 相机沿"当前视线方向"前后移动
    ///      (向前滚 = 拉近/放大), 朝向、俯角、滚转一律不写 —— 画面不会歪、不会变成俯视,
    ///      只是把同一个视角拉近/拉远;
    ///   3) <b>F1(可改键) = 恢复原来的视角</b>: 立刻回到游戏自己的机位, 并把相机交还给游戏。
    ///
    /// 为什么这么实现(本作实测约束, 详见 docs/SDK-相机.md / docs/mod-FreeCameraMod.md):
    ///   - Cinemachine Brain 位于失活对象, 靠 vcam 抢优先级不生效, 只有"每帧直接写主相机"可靠;
    ///   - 游戏自己的相机逻辑每帧都写相机, 所以我们的写入要放在 UpdateService 的
    ///     LateUpdate(实际时机 PostLateUpdate) 才不会被覆盖;
    ///   - 游戏自己完全没用滚轮(反编译确认没有任何 "Mouse ScrollWheel" 读取), 所以滚轮是
    ///     "白拿"的一个轴, 不会和游戏的操作打架。
    ///
    /// 安全保证:
    ///   - 只读输入(滚轮 + 热键), 不 Capture、不拦截、不改键位;
    ///   - 只写相机位置(+ 拉远时按需抬高 farClip 防远处被裁), 从不写朝向/FOV/nearClip;
    ///   - 每帧以"游戏本帧写下的位姿"为基准再加偏移, 所以缩放期间游戏的鼠标拖拽平移 /
    ///     键盘平移仍然有效(一边拉远看全图, 一边照样拖地图);
    ///   - 结束缩放(滚轮滚回原位 / 按复原键 / 切场景 / 换相机 / mod 卸载)时, 把"游戏自己的位姿"
    ///     写回去并还原 farClip —— 绝不会把相机永久丢在缩放后的位置。
    ///
    /// 配置: mods\FreeCameraMod\config.json(旧版 v2.x 的键会在启动时自动清理)
    ///   zoomStep / zoomInMax / zoomOutMax / invertWheel / smooth / smoothTime /
    ///   ensureFarClip / requireReadyCamera / maxReadyCameraHeight / notify /
    ///   restoreKey / resetKey
    /// </summary>
    public sealed class ZoomCameraController : ModBase
    {
        // =====================================================================
        // 配置
        // =====================================================================

        /// <summary>每格滚轮拉近/拉远的米数。</summary>
        private float _zoomStep = FreeCameraMath.DefaultZoomStep;

        /// <summary>沿视线向前(放大/拉近)的最大距离(米)。</summary>
        private float _zoomInMax = FreeCameraMath.DefaultZoomInMax;

        /// <summary>沿视线向后(缩小/拉远)的最大距离(米)。</summary>
        private float _zoomOutMax = FreeCameraMath.DefaultZoomOutMax;

        /// <summary>反转滚轮方向(默认 false: 向前滚 = 拉近)。</summary>
        private bool _invertWheel;

        /// <summary>是否平滑过渡(默认 true: 一格一格滚不会一跳一跳)。</summary>
        private bool _smooth = true;

        /// <summary>平滑时间常数(秒)。</summary>
        private float _smoothTime = 0.12f;

        /// <summary>拉远时是否按需抬高 farClip(防止远处棋盘被裁掉; 退出时还原)。</summary>
        private bool _ensureFarClip = true;

        /// <summary>菜单/过渡期的占位相机不响应滚轮(默认 true)。</summary>
        private bool _requireReadyCamera = true;

        /// <summary>占位相机判定: 朝向为 (0,0,0) 且高度高于这个值(米)。</summary>
        private float _maxReadyCameraHeight = 500f;

        /// <summary>是否弹屏幕提示(滚轮接管/复原各一条)。</summary>
        private bool _notify = true;

        /// <summary>复原键(默认 F1; 按下即回到游戏原来的视角)。</summary>
        private KeyCode _restoreKey = KeyCode.F1;

        /// <summary>第二个复原键(默认 F2; 设成 None 就只认 restoreKey)。</summary>
        private KeyCode _resetKey = KeyCode.F2;

        // =====================================================================
        // 运行态
        // =====================================================================

        private ModConfig _config;

        /// <summary>是否正在覆盖相机(偏移非 0 时会一直覆盖到复原为止)。</summary>
        private bool _active;

        /// <summary>当前缩放偏移(米): &gt;0 = 沿视线向前(放大/拉近), &lt;0 = 向后(缩小/拉远)。</summary>
        private float _offset;

        /// <summary>滚轮直接改的目标偏移; <see cref="_offset"/> 平滑逼近它。</summary>
        private float _targetOffset;

        /// <summary>被我们覆盖的相机。</summary>
        private Camera _driven;

        /// <summary>接管前的镜头参数快照(farClip 还原用)。</summary>
        private CameraState _savedState;

        /// <summary>我们是否抬高过 farClip(只有抬过才需要还原)。</summary>
        private bool _farClipRaised;

        /// <summary>最近一次识别到的"游戏自己的机位"(缩放基准)。</summary>
        private Vector3 _basePosition;

        /// <summary>最近一次识别到的"游戏自己的朝向"。</summary>
        private Vector3 _baseEuler;

        /// <summary>是否已经识别出基准位姿。</summary>
        private bool _hasBasePose;

        /// <summary>最后一次写进相机的缩放后位置(用来分辨"相机里的值是我们写的还是游戏写的")。</summary>
        private Vector3 _lastWrittenPosition;

        private float _lastWrittenYaw;
        private float _lastWrittenPitch;
        private bool _hasWrittenPose;

        /// <summary>本帧要复原(真正处理放在 OnLateUpdate, 那时相机里是"游戏本帧的位姿")。</summary>
        private bool _restoreRequested;
        private string _restoreReason = "";

        /// <summary>本次接管的写入帧数(校验日志用)。</summary>
        private int _frames;

        /// <summary>本次接管是否已经提示过"滚轮缩放中"。</summary>
        private bool _zoomNotified;

        /// <summary>本次启动是否补写过配置/清理过旧键。</summary>
        private bool _configFixed;

        /// <summary>"当前是菜单相机"的提示只记一次。</summary>
        private bool _placeholderWarned;

        public override string Name { get { return "自由相机"; } }

        public override string Version { get { return "2.2.1"; } }

        // =====================================================================
        // 生命周期
        // =====================================================================

        /// <summary>初始化(主线程)。</summary>
        public override void OnInitialize()
        {
            _config = Config;
            LoadConfig();

            // 相机变化时结束接管 —— 换相机/切场景后绝不把旧偏移套到新相机上
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

            Log.Info("自由相机(滚轮缩放)已就绪: 滚轮" + (_invertWheel ? "下" : "上") + "=拉近, 每格 " +
                     F(_zoomStep) + "m (向前上限 " + F(_zoomInMax) + "m / 向后上限 " + F(_zoomOutMax) + "m), " +
                     "平滑=" + (_smooth ? F(_smoothTime) + "s" : "关") + ", " +
                     _restoreKey + "=恢复原来的视角" +
                     (_resetKey != KeyCode.None && _resetKey != _restoreKey ? " (" + _resetKey + " 同效)" : "") +
                     "; 进入游戏时不接管任何东西");
            if (_configFixed && _config != null) Log.Info("配置已更新/迁移: " + _config.Path);
            if (!InputService.IsAvailable)
                Log.Warn("输入后端不可用: 滚轮与热键都读不到(看 SDK 的 INPUT 日志; 本作滚轮只可能来自 Unity 的 mouseScrollDelta / Mouse ScrollWheel 轴)");
        }

        /// <summary>每帧(主线程, Update 时机): 只做输入采集, 不碰相机。</summary>
        public override void OnUpdate()
        {
            try
            {
                // ---- 复原键(默认 F1; 没接管时按了不做事) ----
                if ((_restoreKey != KeyCode.None && InputService.IsKeyPressed(_restoreKey)) ||
                    (_resetKey != KeyCode.None && InputService.IsKeyPressed(_resetKey)))
                {
                    if (_active || _targetOffset != 0f)
                    {
                        // 真正处理放到 OnLateUpdate: 那一刻相机里正是"游戏本帧写下的位姿",
                        // 复原时直接把它写回去, 不会复原到上一帧的缩放位姿。
                        _restoreRequested = true;
                        _restoreReason = "按键 " + (_restoreKey != KeyCode.None ? _restoreKey.ToString() : _resetKey.ToString());
                        return;
                    }
                }

                // ---- 滚轮: 唯一的入场方式(没有任何开关) ----
                float scroll = InputService.GetMouseScroll();
                if (Math.Abs(scroll) > 0.0001f)
                {
                    if (_invertWheel) scroll = -scroll;
                    HandleZoomScroll(scroll);
                }
            }
            catch (Exception e)
            {
                Log.ReportCrash("OnUpdate", e);
            }
        }

        /// <summary>
        /// 每帧 LateUpdate(主线程, 实际时机是 PostLateUpdate)。
        ///
        /// 游戏自己的相机逻辑(Cinemachine Brain 等)跑完之后我们再写, 所以本帧渲染用的
        /// 就是缩放后的机位 —— 这是本作里唯一可靠的接管点(实测 Brain 在失活对象上, 单靠
        /// VirtualCamera 不生效)。只写位置: 朝向/FOV 一律不动, 因此"视角不变, 只是远近变了"。
        /// </summary>
        public override void OnLateUpdate()
        {
            try
            {
                if (!_active) return;

                var camera = (_driven != null && UnityObject.IsAlive(_driven)) ? _driven : CameraService.GetMainCamera();
                if (!UnityObject.IsAlive(camera))
                {
                    // 相机没了(切场景/游戏换相机): 新相机是游戏自己的, 我们只需放弃接管
                    Log.Warn("滚轮缩放: 原相机已失效, 结束接管(新相机由游戏自己控制)");
                    ResetRuntime();
                    return;
                }
                _driven = camera;

                // 1) 识别"游戏本帧写下的位姿"当缩放基准。
                //    要排除我们自己上一帧写进去的值: 若当前位姿与我们上次写入的完全一致,
                //    说明游戏这帧没动相机(静态/暂停), 继续沿用上次识别出的基准 ——
                //    否则会在"已经拉远的位置"上再加一次偏移, 越滚越远。
                Vector3 gamePosition = CameraService.GetPosition(camera);
                Vector3 gameEuler = CameraService.GetEulerAngles(camera);
                bool stillOurWrite = _hasWrittenPose &&
                                     Distance(gamePosition, _lastWrittenPosition) < 0.001f &&
                                     Math.Abs(NormalizeAngle(gameEuler.x - _lastWrittenPitch)) < 0.02f &&
                                     Math.Abs(NormalizeAngle(gameEuler.y - _lastWrittenYaw)) < 0.02f;
                if (!stillOurWrite)
                {
                    _basePosition = gamePosition;
                    _baseEuler = gameEuler;
                    _hasBasePose = true;
                }

                // 2) 复原(按键): 把游戏自己的位姿写回去, 相机交还游戏
                if (_restoreRequested)
                {
                    float restored = _offset;
                    RestoreBasePose(camera);
                    ResetRuntime();
                    Log.Info("滚轮缩放: 已恢复原来的视角" +
                             (string.IsNullOrEmpty(_restoreReason) ? "" : " (" + _restoreReason + ")") +
                             ", 偏移 " + F(restored) + "m -> 0");
                    Notify("自由相机: 已恢复原来的视角");
                    return;
                }

                // 3) 平滑逼近目标偏移(停手后还能顺势滑一小段, 不会一格一跳)
                if (_smooth && _smoothTime > 0f)
                {
                    float dt = UnityTime.DeltaTime;
                    if (dt <= 0f || dt > 0.25f) dt = 1f / 60f;   // 卡顿时限制单帧位移, 避免"瞬移"
                    _offset = FreeCameraMath.SmoothTowards(_offset, _targetOffset, _smoothTime, dt);
                }
                else
                {
                    _offset = _targetOffset;
                }

                // 4) 滚回原位 -> 自动交还相机(不需要再按 F1)
                if (_targetOffset == 0f && FreeCameraMath.IsSettled(_offset, 0f, 0.25f))
                {
                    RestoreBasePose(camera);
                    ResetRuntime();
                    Log.Info("滚轮缩放: 偏移已回到原位, 自动交还相机");
                    Notify("自由相机: 已回到原来的视角 (滚轮归位)");
                    return;
                }

                // 5) 写位置 = 游戏位姿 + forward × 偏移。朝向/俯角/FOV 一律不写。
                float fx, fy, fz;
                FreeCameraMath.ForwardVector(_baseEuler.y, NormalizeAngle(_baseEuler.x), out fx, out fy, out fz);

                float x, y, z;
                FreeCameraMath.DollyPosition(_basePosition.x, _basePosition.y, _basePosition.z,
                    fx, fy, fz, _offset, out x, out y, out z);

                CameraService.SetPosition(camera, new Vector3(x, y, z));

                // 6) 拉远时按需抬高 farClip, 免得远处的棋盘被裁掉(退出时还原成原值)
                EnsureFarClip(camera);

                _lastWrittenPosition = new Vector3(x, y, z);
                _lastWrittenYaw = _baseEuler.y;
                _lastWrittenPitch = NormalizeAngle(_baseEuler.x);
                _hasWrittenPose = true;

                if (!_zoomNotified)
                {
                    _zoomNotified = true;
                    Notify("自由相机: 滚轮缩放中 (" + _restoreKey + " 恢复原视角)");
                }

                _frames++;
                if (_frames == 60 || _frames == 300) LogZoomCheck(camera, gamePosition, gameEuler);
            }
            catch (Exception e)
            {
                Log.ReportCrash("OnLateUpdate", e);
            }
        }

        /// <summary>场景加载完成: 新场景 = 新相机, 偏移一律清零, 绝不跨场景沿用。</summary>
        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!_active) return;
            Log.Info("滚轮缩放: 场景切换(" + (scene.IsValid() ? scene.name : "?") + "), 结束接管并清零偏移");
            ResetRuntime();
        }

        /// <summary>卸载: 把相机还给游戏(不还原 enabled, 见 CameraState.Restore 的说明)。</summary>
        public override void OnUnload()
        {
            if (_active)
            {
                var camera = CameraService.GetMainCamera();
                if (UnityObject.IsAlive(camera)) RestoreBasePose(camera);
                ResetRuntime();
                Log.Info("滚轮缩放: 卸载前已把相机交还给游戏");
            }
            if (_config != null) _config.SaveIfDirty();
            Log.Info("自由相机已卸载");
        }

        // =====================================================================
        // 缩放
        // =====================================================================

        /// <summary>
        /// 滚轮来了: 先确保接管, 再把目标偏移推一格。
        /// 约定: 滚轮向上(正增量) = 沿视线向前 = 放大/拉近。
        /// </summary>
        private void HandleZoomScroll(float scroll)
        {
            if (!_active)
            {
                var camera = CameraService.GetMainCamera();
                if (!UnityObject.IsAlive(camera))
                {
                    Log.Warn("滚轮缩放: 当前没有可用主相机, 忽略这次滚轮");
                    return;
                }

                // 菜单/加载过渡期的占位相机(实测停在 Y≈1002.75、euler=(0,0,0)): 缩放它毫无意义
                if (_requireReadyCamera &&
                    IsPlaceholderCamera(CameraService.GetPosition(camera), CameraService.GetEulerAngles(camera)))
                {
                    if (!_placeholderWarned)
                    {
                        _placeholderWarned = true;
                        Log.Info("滚轮缩放: 当前是菜单/过渡相机, 等进入棋盘后再滚" +
                                 "(requireReadyCamera=false 可关掉这个判断)");
                    }
                    return;
                }

                Activate(camera);
            }

            float before = _targetOffset;
            _targetOffset = FreeCameraMath.ApplyScrollToZoom(_targetOffset, scroll, _zoomStep,
                -Math.Abs(_zoomOutMax), Math.Abs(_zoomInMax));
            if (_targetOffset == before) return;   // 已经顶到极限: 静默, 不刷提示

            if (!_smooth) _offset = _targetOffset;
            Log.Debug("滚轮" + (scroll > 0f ? "↑" : "↓") + " 缩放偏移 " + F(before) + "m -> " + F(_targetOffset) +
                      "m (每格 " + F(_zoomStep) + "m)");
        }

        /// <summary>开始接管: 记下镜头参数快照, 之后每帧在 PostLateUpdate 按偏移写位置。</summary>
        private void Activate(Camera camera)
        {
            _driven = camera;
            _savedState = CameraService.CaptureState(camera);
            _farClipRaised = false;
            _hasWrittenPose = false;
            _hasBasePose = false;      // 第一帧在 OnLateUpdate 里从"游戏本帧的位姿"重新识别基准
            _frames = 0;
            _zoomNotified = false;
            _offset = 0f;
            _targetOffset = 0f;
            _active = true;
            Log.Info("滚轮缩放: 开始接管相机(只改位置, 不动朝向/FOV)" +
                     (_savedState.Valid ? " 进入前: " + _savedState.Describe() : ""));
        }

        /// <summary>把"游戏自己的位姿"写回相机(只在结束接管时用一次), 并还原我们改过的镜头参数。</summary>
        private void RestoreBasePose(Camera camera)
        {
            try
            {
                // 朝向我们从来没写过, 所以这里也不用写(写回去也只是同一个值)
                if (_hasBasePose) CameraService.SetPosition(camera, _basePosition);
            }
            catch (Exception e)
            {
                Log.ReportCrash("RestoreBasePose", e);
            }
            RestoreFarClip(camera);
        }

        /// <summary>
        /// 拉远时把 farClip 抬到"仍能看到原来的瞄准点": farClip = 原值 + |偏移|。
        /// 只在需要抬高时写一次, 结束时按快照还原(ensureFarClip=false 就完全不碰裁剪面)。
        /// </summary>
        private void EnsureFarClip(Camera camera)
        {
            if (!_ensureFarClip || !_savedState.Valid) return;
            float baseFar = _savedState.FarClipPlane;
            if (baseFar <= 0.01f) return;

            float wanted = baseFar + Math.Abs(_offset);
            if (wanted <= baseFar + 0.5f) return;
            if (CameraService.SetFarClipPlane(camera, wanted)) _farClipRaised = true;
        }

        private void RestoreFarClip(Camera camera)
        {
            if (!_farClipRaised) return;
            _farClipRaised = false;
            if (_savedState.Valid && CameraService.SetFarClipPlane(camera, _savedState.FarClipPlane))
                Log.Info("滚轮缩放: farClip 已还原为 " + F(_savedState.FarClipPlane));
        }

        /// <summary>清空运行态(不碰相机)。</summary>
        private void ResetRuntime()
        {
            _active = false;
            _driven = null;
            _offset = 0f;
            _targetOffset = 0f;
            _hasBasePose = false;
            _hasWrittenPose = false;
            _restoreRequested = false;
            _restoreReason = "";
            _zoomNotified = false;
            _frames = 0;
            _farClipRaised = false;
            _savedState = default(CameraState);
        }

        /// <summary>
        /// 接管校验(排查"看起来没生效"用): 写完立刻回读, 偏差 ≈ 0 = 本帧渲染用的就是缩放后的机位;
        /// 基准位姿每帧都在变 = 游戏自己的鼠标/键盘操作仍然有效。
        /// </summary>
        private void LogZoomCheck(Camera camera, Vector3 gamePosition, Vector3 gameEuler)
        {
            Vector3 actual = CameraService.GetPosition(camera);
            float error = Distance(actual, _lastWrittenPosition);
            Log.Info("缩放校验(第" + _frames + "帧): 游戏位姿=(" + F(gamePosition.x) + "," + F(gamePosition.y) + "," +
                     F(gamePosition.z) + ") euler=(" + F(gameEuler.x) + "," + F(gameEuler.y) + "," + F(gameEuler.z) +
                     ") | 偏移=" + F(_offset) + "m 目标=" + F(_targetOffset) + "m 写入=(" +
                     F(_lastWrittenPosition.x) + "," + F(_lastWrittenPosition.y) + "," + F(_lastWrittenPosition.z) +
                     ") 实际=(" + F(actual.x) + "," + F(actual.y) + "," + F(actual.z) + ") 偏差=" + F(error) + "m" +
                     (error < 0.5f ? " (写入生效)" : " (写入未生效!)"));
            if (error > 0.5f)
                Log.Warn("缩放位姿没写进相机: 相机可能拒绝外部写入(偏差 " + F(error) + "m)");
        }

        // =====================================================================
        // 相机事件
        // =====================================================================

        private void OnMainCameraLost()
        {
            if (!_active) return;
            Log.Warn("滚轮缩放: 主相机消失, 结束接管(下一帧由游戏自己接回相机)");
            ResetRuntime();
        }

        private void OnMainCameraChanged(Camera camera)
        {
            if (!_active) return;
            Log.Info("滚轮缩放: 主相机更换, 结束接管并清零偏移");
            ResetRuntime();
        }

        // =====================================================================
        // 配置
        // =====================================================================

        /// <summary>旧版(v2.x)"俯瞰视角"用过的键: 新版一个都不用, 读到就删, 免得用户以为改了有用。</summary>
        private static readonly string[] LegacyKeys = new[]
        {
            "mode", "height", "heightStep", "pitch", "fov", "boardHeight", "aimDistance",
            "maxTargetDistance", "autoActivate", "autoActivateDelay",
            "orbitDistance", "distanceStep", "speed", "rotationSpeed", "invertY", "lockCursor",
            "nearClip", "farClip"
        };

        private void LoadConfig()
        {
            if (_config == null) return;

            // 旧键迁移: 只有 toggleKey(旧版的开关热键, 正对应现在的复原键)会被继承, 其余直接删
            string legacyToggle = _config.GetString("toggleKey", null);
            bool legacyRemoved = false;
            foreach (string key in LegacyKeys)
            {
                if (_config.Remove(key)) legacyRemoved = true;
            }

            _zoomStep = _config.GetFloat("zoomStep", _zoomStep);
            _zoomInMax = _config.GetFloat("zoomInMax", _zoomInMax);
            _zoomOutMax = _config.GetFloat("zoomOutMax", _zoomOutMax);
            _invertWheel = _config.GetBool("invertWheel", _invertWheel);
            _smooth = _config.GetBool("smooth", _smooth);
            _smoothTime = _config.GetFloat("smoothTime", _smoothTime);
            _ensureFarClip = _config.GetBool("ensureFarClip", _ensureFarClip);
            _requireReadyCamera = _config.GetBool("requireReadyCamera", _requireReadyCamera);
            _maxReadyCameraHeight = _config.GetFloat("maxReadyCameraHeight", _maxReadyCameraHeight);
            _notify = _config.GetBool("notify", _notify);

            // 热键: restoreKey(新名) 优先, 没有就继承旧版的 toggleKey(默认 F1); resetKey 是第二个复原键
            string restore = _config.GetString("restoreKey", null);
            if (string.IsNullOrEmpty(restore)) restore = legacyToggle;
            _restoreKey = ParseKey(restore, _restoreKey);
            _resetKey = ParseKey(_config.GetString("resetKey", "F2"), _resetKey);
            if (_config.Remove("toggleKey")) legacyRemoved = true;

            // 数值兜底: 配置写坏也不能变成"滚轮没反应"或"一滚飞出去"
            if (!IsUsable(_zoomStep)) _zoomStep = FreeCameraMath.DefaultZoomStep;
            if (!IsUsable(_zoomInMax)) _zoomInMax = FreeCameraMath.DefaultZoomInMax;
            if (!IsUsable(_zoomOutMax)) _zoomOutMax = FreeCameraMath.DefaultZoomOutMax;
            if (!IsUsable(_smoothTime)) _smoothTime = 0.12f;
            if (!IsUsable(_maxReadyCameraHeight)) _maxReadyCameraHeight = 500f;
            _zoomStep = Math.Min(_zoomStep, 500f);
            _zoomInMax = Math.Min(_zoomInMax, 5000f);
            _zoomOutMax = Math.Min(_zoomOutMax, 20000f);
            _smoothTime = Math.Min(_smoothTime, 2f);

            WriteDefaults(legacyRemoved);
        }

        /// <summary>把当前(已兜底的)配置写回文件: 缺键或清掉了旧键时落盘一次, 让用户能直接改。</summary>
        private void WriteDefaults(bool force)
        {
            if (!force && _config.Has("zoomStep") && _config.Has("restoreKey")) return;

            _config.Set("zoomStep", _zoomStep);
            _config.Set("zoomInMax", _zoomInMax);
            _config.Set("zoomOutMax", _zoomOutMax);
            _config.Set("invertWheel", _invertWheel);
            _config.Set("smooth", _smooth);
            _config.Set("smoothTime", _smoothTime);
            _config.Set("ensureFarClip", _ensureFarClip);
            _config.Set("requireReadyCamera", _requireReadyCamera);
            _config.Set("maxReadyCameraHeight", _maxReadyCameraHeight);
            _config.Set("notify", _notify);
            _config.Set("restoreKey", _restoreKey.ToString());
            _config.Set("resetKey", _resetKey.ToString());
            _config.SaveIfDirty();
            _configFixed = true;
        }

        private static bool IsUsable(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
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

        /// <summary>
        /// 是否是"还没就绪"的菜单/过渡相机(实测 euler=(0,0,0) 且停在 Y≈1002.75 或原点)。
        /// 只用于滚轮入场前的过滤: 免得在菜单/加载界面把过渡相机拉来拉去。
        /// </summary>
        private bool IsPlaceholderCamera(Vector3 position, Vector3 euler)
        {
            if (Math.Abs(NormalizeAngle(euler.x)) >= 0.01f) return false;
            if (Math.Abs(NormalizeAngle(euler.y)) >= 0.01f) return false;
            return position.IsZero() || position.y > _maxReadyCameraHeight;
        }

        // =====================================================================
        // 小工具
        // =====================================================================

        private void Notify(string text)
        {
            if (!_notify) return;
            try { UiService.Notify(text, UiNotificationLevel.Info, 1.5f, Context); }
            catch { }
        }

        private static float Distance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
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
