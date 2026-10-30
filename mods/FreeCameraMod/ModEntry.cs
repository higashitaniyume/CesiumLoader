using System;
using System.Collections.Generic;
using System.Globalization;
using CesiumLoader.SDK.Cameras;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Events;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Scheduling;
using CesiumLoader.SDK.UserInterface;
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
    /// 滚轮缩放 + 左右方向键旋转地图(v2.6.0 新增旋转)。
    ///
    /// 要的行为:
    ///   1) 进游戏后<b>什么都不做</b>: 画面就是游戏自己的默认视角, 没有开关、没有自动接管;
    ///   2) <b>滚轮 = 放大/缩小, 且保持原来的视角</b>: 相机沿"当前视线方向"前后移动
    ///      (向前滚 = 拉近/放大), 朝向、俯角、滚转一律不写 —— 画面不会歪、不会变成俯视,
    ///      只是把同一个视角拉近/拉远;
    ///   3) <b>按住 ← / →(可改键) = 旋转地图</b>: 相机绕"当前观察点"的竖直线环绕,
    ///      观察点始终留在画面中心, 俯角/滚转保持不变 —— 看起来就是整张棋盘在转向;
    ///   4) <b>F1(可改键) = 恢复原来的视角</b>: 缩放偏移与旋转角一起归零,
    ///      立刻回到游戏自己的机位, 并把相机交还给游戏;
    ///   5) <b>autoFollow=false(可选)</b>: 关掉游戏自带的自动跟随, 见下。
    ///
    /// 为什么这么实现(本作实测约束, 详见 docs/SDK-相机.md / docs/mod-FreeCameraMod.md):
    ///   - 菜单/过渡期 Cinemachine Brain 可能在失活对象上, 靠 vcam 抢优先级不生效,
    ///     所以缩放/旋转只有"每帧直接写主相机"可靠; 战斗内角色相机的跟随仍由 vcam 驱动;
    ///   - 游戏自己的相机逻辑每帧都写相机, 所以我们的写入要放在 UpdateService 的
    ///     LateUpdate(实际时机 PostLateUpdate) 才不会被覆盖;
    ///   - 旋转中心取"当前 vcam 的 Follow 目标"(自由相机对象 / 角色)。游戏自己的
    ///     GM 相机(Core.Camera.GMFreeCamera)也是绕 Follow 目标转, 所以这是本作认可的旋转方式;
    ///   - 游戏自己用 ←/→ 读 "Horizontal" 轴做键盘平移(FreeCameraObject.HandleMoveCamera),
    ///     按住旋转键时同一次按键也会推着自由相机往侧面走。因此 blockPanWhileRotate=true(默认)
    ///     会把自由相机对象的位置拉回本帧开始前, 让"左右键 = 只旋转地图";
    ///   - 游戏的拖拽/键盘平移用的是自由相机对象的<b>世界</b>轴, 而画面已经转了 yawDelta;
    ///     不换算的话旋转后平移方向会歪同样的角度, 所以每帧把实际位移绕基准点转 yawDelta 度。
    ///
    /// 安全保证:
    ///   - 只读输入(滚轮 + 热键), 不 Capture、不拦截、不改键位;
    ///   - 缩放只写相机位置; 旋转额外写朝向(yaw 加同一个角度), 俯角/滚转/FOV/nearClip 不写;
    ///   - autoFollow=false 时为锁定画面会同时写回位置和朝向(默认模式不写);
    ///   - 每帧以"游戏本帧写下的位姿"为基准再加偏移/旋转, 所以旋转期间游戏的鼠标拖拽平移 /
    ///     键盘平移仍然有效(一边转地图, 一边照样拖), 且方向按地图角度换算;
    ///   - 结束接管(滚轮回原位且旋转归零 / 按复原键 / 切场景 / 换相机 / mod 卸载)时,
    ///     把"游戏自己的位姿"写回去并还原 farClip —— 绝不会把相机永久留在改动后的状态。
    ///
    /// 配置: mods\FreeCameraMod\config.json(旧版 v2.x 的键会在启动时自动清理)
    ///   autoFollow / zoomStep / zoomInMax / zoomOutMax / invertWheel / smooth / smoothTime /
    ///   ensureFarClip / requireReadyCamera / maxReadyCameraHeight / notify /
    ///   restoreKey / resetKey / rotateLeftKey / rotateRightKey / rotateSpeed / rotateStep /
    ///   blockPanWhileRotate
    ///
    /// 自动跟随(autoFollow, 默认 true):
    ///   - true = 完全保持游戏原样, 角色移动时镜头照常跟着走;
    ///   - false = 锁定当前画面: 角色相机的 Follow 换成固定锚点(角色移动/换角色不带动镜头),
    ///     同时在 PostLateUpdate 把主相机位姿锁回固定值(回合开始/刷怪/演出运镜也不移动画面)。
    ///     锚点/锁定位置每个场景只取一次; 只有玩家主动拖拽/按键平移自由相机时才更新。
    /// </summary>
    public sealed class ZoomCameraController : ModBase
    {
        // =====================================================================
        // 配置
        // =====================================================================

        /// <summary>是否保持游戏自带的"角色移动时镜头跟随"(默认 true)。</summary>
        private bool _autoFollow = true;

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

        /// <summary>向左旋转地图的按键(默认 LeftArrow; 设成 None 可关掉)。</summary>
        private KeyCode _rotateLeftKey = KeyCode.LeftArrow;

        /// <summary>向右旋转地图的按键(默认 RightArrow; 设成 None 可关掉)。</summary>
        private KeyCode _rotateRightKey = KeyCode.RightArrow;

        /// <summary>按住旋转键时的旋转速度(度/秒)。</summary>
        private float _rotateSpeed = FreeCameraMath.DefaultRotateSpeed;

        /// <summary>点按一次旋转键的最小步进(度); 0 = 只保留按住连续旋转。</summary>
        private float _rotateStep = FreeCameraMath.DefaultRotateStep;

        /// <summary>反转左右方向(默认 false: → 让视点向右转; true: → 让地图向右转)。</summary>
        private bool _invertRotate;

        /// <summary>旋转时是否抵掉游戏自己的方向键平移(默认 true: 左右键只旋转, 不把地图推走)。</summary>
        private bool _blockPanWhileRotate = true;

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

        /// <summary>"当前没有可用主相机"的提示只记一次(按住旋转键时不要每帧刷日志)。</summary>
        private bool _noCameraWarned;

        /// <summary>"找不到旋转中心"的提示只记一次。</summary>
        private bool _pivotWarned;

        // ---- 旋转地图(左右方向键) ----

        /// <summary>当前实际生效的旋转角(度): &gt;0 = 向右转(相机绕观察点顺时针), &lt;0 = 向左转。</summary>
        private float _yawDelta;

        /// <summary>按键直接累加的目标旋转角; <see cref="_yawDelta"/> 平滑逼近它。</summary>
        private float _targetYawDelta;

        /// <summary>本帧是否按住了旋转键(在 OnUpdate 里采一次, 供锁定/平移判断复用)。</summary>
        private bool _rotateHeldNow;

        /// <summary>我们是否给相机写过朝向(只有写过才需要在交还时还原)。</summary>
        private bool _rotationWritten;

        /// <summary>是否已经提示过"正在旋转地图"。</summary>
        private bool _rotationNotified;

        /// <summary>最近一次旋转用的观察点(自由相机对象 / 角色位置)。</summary>
        private Vector3 _orbitPivot;

        /// <summary>是否已有可用的旋转中心(换场景/换相机后失效)。</summary>
        private bool _hasOrbitPivot;

        /// <summary>是否正在跟踪/换算自由相机对象的位置(旋转或按住旋转键时)。</summary>
        private bool _panTracking;

        /// <summary>自由相机对象位置快照(本帧开始前的位置); 每帧位移按旋转角换算。</summary>
        private Vector3 _panSnapshot;

        /// <summary>参与换算的自由相机对象(游戏换相机后重新取)。</summary>
        private Transform _panTarget;

        // =====================================================================
        // 自动跟随(仅 autoFollow=false 时使用)
        // =====================================================================

        /// <summary>固定跟随锚点; 角色相机把 Follow 指向它后, 角色移动镜头就不动了。</summary>
        private GameObject _followAnchor;

        /// <summary>缓存的 CinemachineBrain(FindBrain 会全场景搜索, 不能每帧调用)。</summary>
        private object _brain;

        /// <summary>解析 Brain 时对应的主相机(换相机后缓存必须失效)。</summary>
        private Camera _brainOwner;

        /// <summary>找不到 Brain 时的重试倒计时(帧), 避免在菜单里每帧全场景搜索。</summary>
        private int _brainProbeCooldown;

        /// <summary>上一次处理过的活动 vcam instanceID(换相机/换玩家时重新检查)。</summary>
        private int _lastFollowVcamId;

        /// <summary>是否已经取过锚点位姿(每个场景只取一次; 之后换角色/换角色相机都不再移动镜头)。</summary>
        private bool _hasFollowAnchorPose;

        /// <summary>上一帧的游戏相机位置(排除滚轮缩放偏移), 用于自由相机/演出运镜时让锚点跟着走。</summary>
        private Vector3 _lastGameCameraPosition;

        /// <summary>上一帧游戏相机位置是否有效。</summary>
        private bool _hasLastGameCameraPosition;

        /// <summary>上一帧是否在混合(只在混合开始时扫描一次角色相机)。</summary>
        private bool _wasBlending;

        /// <summary>是否已锁定主相机位姿(autoFollow=false 时把游戏自动运镜也挡掉)。</summary>
        private bool _hasCameraLock;

        /// <summary>锁定的主相机世界位置。</summary>
        private Vector3 _lockedCameraPosition;

        /// <summary>锁定的主相机世界欧拉角。</summary>
        private Vector3 _lockedCameraEuler;

        /// <summary>锁定的棋盘主相机(演出期间游戏会把它失活, 但位姿仍要锁住)。</summary>
        private Camera _lockedMainCamera;

        /// <summary>演出/战斗用的额外相机(本作的 PKMainCamera), 启用时冻结在开场机位。</summary>
        private readonly List<ExtraCameraLock> _extraCameraLocks = new List<ExtraCameraLock>();

        /// <summary>额外相机列表的重扫倒计时(帧)。</summary>
        private int _extraCameraScanCooldown;

        /// <summary>一台额外相机的冻结状态。</summary>
        private sealed class ExtraCameraLock
        {
            public Camera Camera;
            public bool HasPose;
            public int Delay;
            public Vector3 Position;
            public Vector3 Euler;
            public float FieldOfView;
            public bool Orthographic;
            public float OrthographicSize;
        }

        /// <summary>本帧玩家是否在主动拖拽/按键移动自由相机(每帧只采集一次输入)。</summary>
        private bool _playerMovingFreeCamera;

        /// <summary>上一帧鼠标屏幕坐标(用于区分"拖拽"和"点一下 UI")。</summary>
        private Vector2 _lastMousePosition;

        /// <summary>上一帧鼠标屏幕坐标是否有效。</summary>
        private bool _hasLastMousePosition;

        /// <summary>被我们改过 Follow 的 vcam 及原始目标(卸载/切场景时还原)。</summary>
        private readonly List<FollowOverride> _followOverrides = new List<FollowOverride>();

        /// <summary>Follow 改写失败只记一条日志。</summary>
        private bool _followWarned;

        /// <summary>一次 Follow 改写记录: vcam 与它原本的 Follow/LookAt。</summary>
        private sealed class FollowOverride
        {
            public object Vcam;
            public int VcamId;
            public Transform OriginalFollow;
            public Transform OriginalLookAt;
            public bool LookAtOverridden;
        }

        public override string Name { get { return "自由相机"; } }

        public override string Version { get { return "2.6.0"; } }

        // =====================================================================
        // 生命周期
        // =====================================================================

        /// <summary>初始化(主线程)。</summary>
        public override void OnInitialize()
        {
            _config = Config;
            LoadConfig(isInitial: true);

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

            Log.Info("自由相机(滚轮缩放 + 方向键旋转)已就绪: 滚轮" + (_invertWheel ? "下" : "上") + "=拉近, 每格 " +
                     F(_zoomStep) + "m (向前上限 " + F(_zoomInMax) + "m / 向后上限 " + F(_zoomOutMax) + "m), " +
                     "平滑=" + (_smooth ? F(_smoothTime) + "s" : "关") + ", " +
                     "旋转=" + DescribeRotateKeys() + " (" + F(_rotateSpeed) + "°/秒, 点按 " + F(_rotateStep) + "°" +
                     (_invertRotate ? ", 反向" : "") + ")" +
                     (_blockPanWhileRotate ? ", 旋转时抵掉游戏方向键平移" : "") + ", " +
                     _restoreKey + "=恢复原来的视角" +
                     (_resetKey != KeyCode.None && _resetKey != _restoreKey ? " (" + _resetKey + " 同效)" : "") +
                     "; 自动跟随=" + (_autoFollow ? "开(游戏原样, 锁定镜头未启用)" : "关(锁定当前镜头)") +
                     (_autoFollow ? "; 要启用锁定镜头请把 config.json 的 autoFollow 改成 false(热重载即时生效)" : "; 游戏自动运镜不再移动画面, 玩家拖拽/按键平移自由相机仍可用"));
            if (_configFixed && _config != null) Log.Info("配置已更新/迁移: " + _config.Path);
            if (!_autoFollow && !CinemachineService.IsAvailable)
                Log.Warn("自动跟随已配置为关闭, 但没有解析到 Cinemachine: 本功能依赖角色相机的 VirtualCamera, 当前无法生效");
            if (!InputService.IsAvailable)
                Log.Warn("输入后端不可用: 滚轮与热键都读不到(看 SDK 的 INPUT 日志; 本作滚轮只可能来自 Unity 的 mouseScrollDelta / Mouse ScrollWheel 轴)");
        }

        /// <summary>配置文件热重载: 重读字段; autoFollow 切换时还原/重建镜头锁定。</summary>
        public override void OnConfigChanged()
        {
            bool wasAutoFollow = _autoFollow;
            LoadConfig(isInitial: false);

            if (wasAutoFollow != _autoFollow)
            {
                // 交给下一帧 MaintainAutoFollow 按新值重建; 先还原被改写的 Follow/LookAt,
                // 这样 autoFollow 从 false 改回 true 时游戏能立刻恢复自己的跟随。
                ResetFollowOverride();
                Log.Info("自动跟随已" + (_autoFollow ? "恢复(游戏原样)" : "关闭(锁定当前镜头)") + ", 镜头锁定已重置");
            }

            Log.Info("自由相机配置已热重载: 滚轮" + (_invertWheel ? "下" : "上") + "=拉近, 每格 " +
                     F(_zoomStep) + "m, 向前上限 " + F(_zoomInMax) + "m / 向后上限 " + F(_zoomOutMax) + "m, " +
                     "旋转=" + DescribeRotateKeys() + " (" + F(_rotateSpeed) + "°/秒, 点按 " + F(_rotateStep) + "°" +
                     (_invertRotate ? ", 反向" : "") + ") " +
                     (_blockPanWhileRotate ? "抵掉游戏方向键平移" : "保留游戏方向键平移") + ", " +
                     _restoreKey + "=恢复原来的视角");
        }

        /// <summary>每帧(主线程, Update 时机): 只做输入采集, 不碰相机。</summary>
        public override void OnUpdate()
        {
            try
            {
                // 左右方向键同时是游戏"键盘平移"的 Horizontal 轴, 每帧先确定一次,
                // 后面判断"玩家是否在平移自由相机"时要把它们排除(它们只负责旋转地图)。
                _rotateHeldNow = IsRotateHeld();

                // ---- 自动跟随(autoFollow=false 时每帧维护"固定跟随锚点") ----
                MaintainAutoFollow();

                if (UiService.ShouldBlockGameInput)
                {
                    _rotateHeldNow = false;
                    return;
                }

                // ---- 复原键(默认 F1; 没接管时按了不做事) ----
                if ((_restoreKey != KeyCode.None && InputService.IsKeyPressed(_restoreKey)) ||
                    (_resetKey != KeyCode.None && InputService.IsKeyPressed(_resetKey)))
                {
                    if (_active || _targetOffset != 0f || HasRotation)
                    {
                        // 真正处理放到 OnLateUpdate: 那一刻相机里正是"游戏本帧写下的位姿",
                        // 复原时直接把它写回去, 不会复原到上一帧的缩放/旋转位姿。
                        _restoreRequested = true;
                        _restoreReason = "按键 " + (_restoreKey != KeyCode.None ? _restoreKey.ToString() : _resetKey.ToString());
                        return;
                    }
                }

                // ---- 滚轮: 缩放(不需要任何开关) ----
                float scroll = InputService.GetMouseScroll();
                if (Math.Abs(scroll) > 0.0001f)
                {
                    if (_invertWheel) scroll = -scroll;
                    HandleZoomScroll(scroll);
                }

                // ---- 左右方向键: 旋转地图(同样不需要开关) ----
                HandleRotateInput();
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
        /// 就是缩放/旋转后的机位 —— 这是本作里唯一可靠的接管点(实测 Brain 在失活对象上,
        /// 单靠 VirtualCamera 不生效)。<c>autoFollow=false</c> 时这里还会把主相机位姿锁回
        /// 固定视角(游戏自动运镜、回合开始、刷怪都不让画面移动)。
        ///
        /// 写什么:
        ///   - 缩放只写位置(沿视线前后移动, 朝向不变);
        ///   - 旋转是把位置绕观察点转 yawDelta、同时把朝向的 yaw 加同一个角度,
        ///     俯角/滚转/FOV 都不动 —— 观察点留在画面中心, 看起来就是整张地图在转;
        ///   - 和缩放一样, 基准仍然是"游戏本帧写下的位姿", 所以旋转期间玩家的
        ///     鼠标拖拽/键盘平移依然有效。
        /// </summary>
        public override void OnLateUpdate()
        {
            try
            {
                // 自动跟随关闭时: 先把主相机锁回固定视角(玩家正在主动平移自由相机时除外)。
                // 这一步在缩放/旋转之前做, 后面会以锁定位姿作为基准再叠加偏移与旋转。
                bool cameraLocked = UpdateFollowCameraLock();

                if (!_active) return;

                var camera = (_driven != null && UnityObject.IsAlive(_driven)) ? _driven : CameraService.GetMainCamera();
                if (!UnityObject.IsAlive(camera))
                {
                    // 相机没了(切场景/游戏换相机): 新相机是游戏自己的, 我们只需放弃接管
                    Log.Warn("自由相机: 原相机已失效, 结束接管(新相机由游戏自己控制)");
                    ResetRuntime();
                    return;
                }
                _driven = camera;

                // 1) 识别"游戏本帧写下的位姿"当缩放/旋转基准。
                //    要排除我们自己上一帧写进去的值: 若当前位姿与我们上次写入的完全一致,
                //    说明游戏这帧没动相机(静态/暂停), 继续沿用上次识别出的基准 ——
                //    否则会在"已经拉远/转过"的位置上再叠加一次, 越滚越远、越转越多。
                Vector3 gamePosition = CameraService.GetPosition(camera);
                Vector3 gameEuler = CameraService.GetEulerAngles(camera);
                if (cameraLocked)
                {
                    // 基准已经由 UpdateFollowCameraLock 设成锁定位姿, 不再从游戏相机读取
                    _hasBasePose = true;
                }
                else
                {
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
                }

                // 2) 复原(按键): 把游戏自己的位姿写回去, 相机交还游戏
                if (_restoreRequested)
                {
                    float restoredOffset = _offset;
                    float restoredYaw = _yawDelta;
                    RestoreBasePose(camera);
                    ResetRuntime();
                    Log.Info("自由相机: 已恢复原来的视角" +
                             (string.IsNullOrEmpty(_restoreReason) ? "" : " (" + _restoreReason + ")") +
                             ", 偏移 " + F(restoredOffset) + "m -> 0, 旋转 " + F(restoredYaw) + "° -> 0");
                    Notify("自由相机: 已恢复原来的视角");
                    return;
                }

                // 3) 平滑逼近目标(停手后还能顺势滑一小段, 不会一格一跳、一按一转)
                float dt = UnityTime.DeltaTime;
                if (dt <= 0f || dt > 0.25f) dt = 1f / 60f;   // 卡顿时限制单帧位移, 避免"瞬移"
                if (_smooth && _smoothTime > 0f)
                {
                    _offset = FreeCameraMath.SmoothTowards(_offset, _targetOffset, _smoothTime, dt);
                    _yawDelta = FreeCameraMath.SmoothTowards(_yawDelta, _targetYawDelta, _smoothTime, dt);
                }
                else
                {
                    _offset = _targetOffset;
                    _yawDelta = _targetYawDelta;
                }

                // 收敛后把旋转角折回 (-180, 180]: 转很多圈也不会让浮点精度变差, 画面完全不变
                if (FreeCameraMath.IsSettled(_yawDelta, _targetYawDelta, 0.001f))
                {
                    _yawDelta = NormalizeAngle(_yawDelta);
                    _targetYawDelta = _yawDelta;
                }

                // 4) 缩回到原位且没有旋转 -> 自动交还相机(不需要再按 F1)
                bool zoomSettled = _targetOffset == 0f && FreeCameraMath.IsSettled(_offset, 0f, 0.25f);
                bool rotationSettled = FreeCameraMath.IsSettled(_yawDelta, 0f, 0.01f) &&
                                       FreeCameraMath.IsSettled(_targetYawDelta, 0f, 0.01f);
                if (zoomSettled && rotationSettled)
                {
                    RestoreBasePose(camera);
                    ResetRuntime();
                    Log.Info("自由相机: 偏移已回到原位, 自动交还相机");
                    Notify("自由相机: 已回到原来的视角 (滚轮归位)");
                    return;
                }

                // 5) 旋转需要"观察点": 取当前 vcam 的 Follow 目标(自由相机对象 / 角色)。
                //    找不到就退回上一帧的观察点, 再不行就放弃这一帧的旋转(缩放照常)。
                //    只做缩放时不取观察点, 省掉每帧的 Follow 解析。
                float yawDelta = _yawDelta;
                Vector3 pivot = _basePosition;
                if (Math.Abs(yawDelta) > 0.0001f && !TryGetOrbitPivot(out pivot))
                {
                    if (!_pivotWarned)
                    {
                        _pivotWarned = true;
                        Log.Warn("旋转地图: 找不到旋转中心(相机没有 Follow 目标), 暂停旋转; 缩放不受影响");
                    }
                    yawDelta = 0f;
                    _yawDelta = 0f;
                    _targetYawDelta = 0f;
                    pivot = _basePosition;
                }

                // 6) 写位姿 = 绕观察点旋转 + 沿新视线缩放。俯角/滚转/FOV 不动。
                float x, y, z, writtenYaw;
                FreeCameraMath.ComposeOrbitDolly(pivot.x, pivot.z,
                    _basePosition.x, _basePosition.y, _basePosition.z,
                    _baseEuler.y, NormalizeAngle(_baseEuler.x),
                    yawDelta, _offset,
                    out x, out y, out z, out writtenYaw);

                CameraService.SetPosition(camera, new Vector3(x, y, z));

                // 旋转时朝向只加 yaw; 不旋转就完全不写朝向(缩放保持"视角不变")
                if (Math.Abs(yawDelta) > 0.0001f)
                {
                    CameraService.SetEulerAngles(camera,
                        new Vector3(NormalizeAngle(_baseEuler.x), writtenYaw, _baseEuler.z));
                    _rotationWritten = true;
                }

                // 7) 拉远时按需抬高 farClip, 免得远处的棋盘被裁掉(退出时还原成原值)
                EnsureFarClip(camera);

                _lastWrittenPosition = new Vector3(x, y, z);
                _lastWrittenYaw = writtenYaw;
                _lastWrittenPitch = NormalizeAngle(_baseEuler.x);
                _hasWrittenPose = true;

                if (!_zoomNotified && Math.Abs(_offset) > 0.01f)
                {
                    _zoomNotified = true;
                    Notify("自由相机: 滚轮缩放中 (" + _restoreKey + " 恢复原视角)");
                }
                if (!_rotationNotified && Math.Abs(yawDelta) > 0.01f)
                {
                    _rotationNotified = true;
                    Notify("自由相机: 方向键旋转地图中 (" + _restoreKey + " 恢复原视角)");
                }

                _frames++;
                if (_frames == 60 || _frames == 300) LogDriveCheck(camera, gamePosition, gameEuler);

                // 8) 按住旋转键时, 游戏自己的 ←/→ 键盘平移会同时把自由相机往侧面推。
                //    这里把自由相机对象拉回本帧开始前的位置, 让左右键只旋转地图。
                UpdateRotatePan();
            }
            catch (Exception e)
            {
                Log.ReportCrash("OnLateUpdate", e);
            }
        }

        /// <summary>场景加载完成: 新场景 = 新相机, 偏移与旋转一律清零, 绝不跨场景沿用。</summary>
        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 旧场景的相机/锚点已经(或即将)销毁: 先尽量还原, 再丢弃全部自动跟随状态
            ResetFollowOverride();

            if (!_active) return;
            Log.Info("自由相机: 场景切换(" + (scene.IsValid() ? scene.name : "?") + "), 结束接管并清零偏移/旋转");
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
                Log.Info("自由相机: 卸载前已把相机交还给游戏");
            }
            if (_config != null) _config.SaveIfDirty();
            ResetFollowOverride();
            Log.Info("自由相机已卸载");
        }

        // =====================================================================
        // 缩放 / 旋转
        // =====================================================================

        /// <summary>
        /// 滚轮来了: 先确保接管, 再把目标偏移推一格。
        /// 约定: 滚轮向上(正增量) = 沿视线向前 = 放大/拉近。
        /// </summary>
        private void HandleZoomScroll(float scroll)
        {
            if (!TryBeginDrive("滚轮缩放")) return;

            float before = _targetOffset;
            _targetOffset = FreeCameraMath.ApplyScrollToZoom(_targetOffset, scroll, _zoomStep,
                -Math.Abs(_zoomOutMax), Math.Abs(_zoomInMax));
            if (_targetOffset == before) return;   // 已经顶到极限: 静默, 不刷提示

            if (!_smooth) _offset = _targetOffset;
            Log.Debug("滚轮" + (scroll > 0f ? "↑" : "↓") + " 缩放偏移 " + F(before) + "m -> " + F(_targetOffset) +
                      "m (每格 " + F(_zoomStep) + "m)");
        }

        /// <summary>
        /// 左右方向键来了: 先确保接管, 再把目标旋转角往左/右推。
        /// 约定(默认): → = 让视点向右转(yaw 增大), ← = 让视点向左转; 地图在画面里往反方向转。
        /// <c>invertRotate=true</c> 时反过来(→ = 地图向右转)。方向键本身永远按"左/右"读, 不受反转影响。
        /// 按住 = 按 <c>rotateSpeed</c> 连续转; 点按 = 至少转 <c>rotateStep</c> 度(所以按一下也看得出来)。
        /// </summary>
        private void HandleRotateInput()
        {
            float direction = RotateDirection();
            if (direction == 0f) return;
            if (_invertRotate) direction = -direction;

            if (!TryBeginDrive("方向键旋转")) return;

            // 点按步进只在这一帧刚按下时补一次(按的是哪个键决定方向, 反转只改角度符号)
            bool justPressed = IsKeyPressed(_rotateLeftKey) || IsKeyPressed(_rotateRightKey);
            float step = justPressed ? FreeCameraMath.ComposeRotateStep(direction, _rotateStep) : 0f;

            float delta = FreeCameraMath.ComposeRotateDelta(direction, _rotateSpeed, UnityTime.DeltaTime) + step;
            if (delta == 0f) return;

            float before = _targetYawDelta;
            _targetYawDelta += delta;
            if (!_smooth) _yawDelta = _targetYawDelta;

            // 连续转动时不刷屏; 调试日志只在 Debug 级别
            Log.Debug("方向键旋转 " + (direction < 0f ? "←" : "→") + " 目标 " + F(before) + "° -> " +
                      F(_targetYawDelta) + "° (速度 " + F(_rotateSpeed) + "°/秒" +
                      (step != 0f ? ", 点按步进 " + F(step) + "°" : "") + ")");
        }

        /// <summary>按住旋转键、且确实接管了相机时才写相机; 菜单/没相机时忽略。</summary>
        private bool TryBeginDrive(string reason)
        {
            if (_active && UnityObject.IsAlive(_driven)) return true;

            var camera = CameraService.GetMainCamera();
            if (!UnityObject.IsAlive(camera))
            {
                if (!_noCameraWarned)
                {
                    _noCameraWarned = true;
                    Log.Warn("自由相机: 当前没有可用主相机, 忽略这次" + reason);
                }
                return false;
            }

            // 菜单/加载过渡期的占位相机(实测停在 Y≈1002.75、euler=(0,0,0)): 动它毫无意义
            if (_requireReadyCamera &&
                IsPlaceholderCamera(CameraService.GetPosition(camera), CameraService.GetEulerAngles(camera)))
            {
                if (!_placeholderWarned)
                {
                    _placeholderWarned = true;
                    Log.Info("自由相机: 当前是菜单/过渡相机, 等进入棋盘后再滚/按方向键" +
                             "(requireReadyCamera=false 可关掉这个判断)");
                }
                return false;
            }

            Activate(camera, reason);
            return true;
        }

        /// <summary>开始接管: 记下镜头参数快照, 之后每帧在 PostLateUpdate 按偏移/旋转写位姿。</summary>
        private void Activate(Camera camera, string reason)
        {
            _driven = camera;
            _savedState = CameraService.CaptureState(camera);
            _farClipRaised = false;
            _hasWrittenPose = false;
            _hasBasePose = false;      // 第一帧在 OnLateUpdate 里从"游戏本帧的位姿"重新识别基准
            _frames = 0;
            _zoomNotified = false;
            _rotationNotified = false;
            _noCameraWarned = false;
            _pivotWarned = false;
            _offset = 0f;
            _targetOffset = 0f;
            _yawDelta = 0f;
            _targetYawDelta = 0f;
            _rotationWritten = false;
            _hasOrbitPivot = false;
            _panTracking = false;
            _panTarget = null;
            _active = true;
            Log.Info("自由相机: 开始接管相机(" + reason + "; 位置随缩放/旋转改写, 俯角/FOV 不动)" +
                     (_savedState.Valid ? " 进入前: " + _savedState.Describe() : ""));
        }

        /// <summary>把"游戏自己的位姿"写回相机(只在结束接管时用一次), 并还原我们改过的镜头参数。</summary>
        private void RestoreBasePose(Camera camera)
        {
            try
            {
                if (!_hasBasePose) return;

                CameraService.SetPosition(camera, _basePosition);
                // 旋转过才写过朝向, 也只有那时才需要把游戏原来的朝向写回去
                if (_rotationWritten)
                    CameraService.SetEulerAngles(camera, new Vector3(_baseEuler.x, _baseEuler.y, _baseEuler.z));
            }
            catch (Exception e)
            {
                Log.ReportCrash("RestoreBasePose", e);
            }
            finally
            {
                RestoreFarClip(camera);
            }
        }

        /// <summary>
        /// 拉远时把 farClip 抬到"仍能看到原来的瞄准点": farClip = 原值 + |偏移|。
        /// 只在需要抬高时写一次, 结束时按快照还原(ensureFarClip=false 就完全不碰裁剪面)。
        /// 旋转不会改变相机到观察点的距离, 所以不参与这里的计算。
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
                Log.Info("自由相机: farClip 已还原为 " + F(_savedState.FarClipPlane));
        }

        /// <summary>清空运行态(不碰相机)。旋转角一起清掉: 下次触发旋转从"游戏当前朝向"重新开始。</summary>
        private void ResetRuntime()
        {
            _active = false;
            _driven = null;
            _offset = 0f;
            _targetOffset = 0f;
            _yawDelta = 0f;
            _targetYawDelta = 0f;
            _rotationWritten = false;
            _rotationNotified = false;
            _hasOrbitPivot = false;
            _orbitPivot = Vector3.zero;
            _hasBasePose = false;
            _hasWrittenPose = false;
            _restoreRequested = false;
            _restoreReason = "";
            _zoomNotified = false;
            _frames = 0;
            _farClipRaised = false;
            _savedState = default(CameraState);
            _rotateHeldNow = false;
            _panTracking = false;
            _panTarget = null;
            _panSnapshot = Vector3.zero;
            _noCameraWarned = false;
            _pivotWarned = false;
        }

        /// <summary>
        /// 接管校验(排查"看起来没生效"用): 写完立刻回读, 偏差 ≈ 0 = 本帧渲染用的就是改过的机位;
        /// 基准位姿每帧都在变 = 游戏自己的鼠标/键盘操作仍然有效。
        /// </summary>
        private void LogDriveCheck(Camera camera, Vector3 gamePosition, Vector3 gameEuler)
        {
            Vector3 actual = CameraService.GetPosition(camera);
            float error = Distance(actual, _lastWrittenPosition);
            Log.Info("自由相机校验(第" + _frames + "帧): 游戏位姿=(" + F(gamePosition.x) + "," + F(gamePosition.y) + "," +
                     F(gamePosition.z) + ") euler=(" + F(gameEuler.x) + "," + F(gameEuler.y) + "," + F(gameEuler.z) +
                     ") | 偏移=" + F(_offset) + "m 目标=" + F(_targetOffset) + "m 旋转=" + F(_yawDelta) + "° 目标=" +
                     F(_targetYawDelta) + "° 写入=(" +
                     F(_lastWrittenPosition.x) + "," + F(_lastWrittenPosition.y) + "," + F(_lastWrittenPosition.z) +
                     ") 实际=(" + F(actual.x) + "," + F(actual.y) + "," + F(actual.z) + ") 偏差=" + F(error) + "m" +
                     (error < 0.5f ? " (写入生效)" : " (写入未生效!)"));
            if (error > 0.5f)
                Log.Warn("自由相机位姿没写进相机: 相机可能拒绝外部写入(偏差 " + F(error) + "m)");
        }

        // =====================================================================
        // 旋转地图
        // =====================================================================

        /// <summary>一次平移换算/回拉的最大水平距离(米): 超过就是游戏在搬自由相机, 不是玩家拖拽/按键平移。</summary>
        private const float MaxPanSuppressDistance = 60f;

        /// <summary>是否按住左/右旋转键(两个一起按时互相抵消, 视为没按)。</summary>
        private bool IsRotateHeld()
        {
            return RotateDirection() != 0f;
        }

        /// <summary>旋转方向: -1 = 向左(←), +1 = 向右(→), 0 = 没按或同时按住。</summary>
        private float RotateDirection()
        {
            float direction = 0f;
            if (_rotateLeftKey != KeyCode.None && InputService.IsKeyHeld(_rotateLeftKey)) direction -= 1f;
            if (_rotateRightKey != KeyCode.None && InputService.IsKeyHeld(_rotateRightKey)) direction += 1f;
            return direction;
        }

        private static bool IsKeyPressed(KeyCode key)
        {
            return key != KeyCode.None && InputService.IsKeyPressed(key);
        }

        /// <summary>当前是否有需要我们维持的旋转(已转过或正在平滑)。</summary>
        private bool HasRotation
        {
            get { return Math.Abs(_yawDelta) > 0.001f || Math.Abs(_targetYawDelta) > 0.001f; }
        }

        /// <summary>日志用: 当前生效的旋转键。</summary>
        private string DescribeRotateKeys()
        {
            if (_rotateLeftKey == KeyCode.None && _rotateRightKey == KeyCode.None) return "关";
            string left = _rotateLeftKey == KeyCode.None ? "(无)" : _rotateLeftKey.ToString();
            string right = _rotateRightKey == KeyCode.None ? "(无)" : _rotateRightKey.ToString();
            return left + "/" + right;
        }

        /// <summary>
        /// 旋转中心: 当前活动 vcam 的 Follow 目标(自由相机对象 / 角色)。
        ///
        /// 本作自己的 GM 相机(<c>Core.Camera.GMFreeCamera</c>)就是绕 Follow 目标转的
        /// (旋转 followTarget, 再给 vcam 写同样的 yaw), 所以这里取同一个点 —— 旋转过程中
        /// 观察点留在画面中心, 看起来才是"地图在转"而不是"相机在飘"。
        /// 取不到时退回上一帧的观察点, 再不行用视线与地面的交点。
        /// </summary>
        private bool TryGetOrbitPivot(out Vector3 pivot)
        {
            pivot = Vector3.zero;

            var brain = GetBrain();
            if (brain != null)
            {
                var vcam = CinemachineService.GetActiveVirtualCamera(brain);
                Transform follow = CinemachineService.GetFollow(vcam);
                if (UnityObject.IsAlive(follow))
                {
                    pivot = TransformService.GetPosition(follow);
                    _orbitPivot = pivot;
                    _hasOrbitPivot = true;
                    return true;
                }
            }

            if (_hasOrbitPivot)
            {
                pivot = _orbitPivot;
                return true;
            }

            // 兜底(没有 vcam/没有 Follow): 用视线与地面(y=0)的交点当观察点
            float fx, fy, fz;
            FreeCameraMath.ForwardVector(_baseEuler.y, NormalizeAngle(_baseEuler.x), out fx, out fy, out fz);
            if (fy < -0.01f)
            {
                float t = -_basePosition.y / fy;
                if (!float.IsNaN(t) && t > 0f && t < 500f)
                {
                    pivot = new Vector3(_basePosition.x + fx * t, 0f, _basePosition.z + fz * t);
                    _orbitPivot = pivot;
                    _hasOrbitPivot = true;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 玩家自由相机对象(Core.FreeCameraObject)的 Transform; 当前活动 vcam 不跟随它时返回 null。
        ///
        /// 注意拿的是"挂着 FreeCameraObject 的那个对象"而不是 vcam 的 Follow 节点: 本作这两者
        /// 可能不是同一个(Follow 可以是子节点), 改子节点只会在父对象里积累局部偏移。
        /// </summary>
        private Transform GetFreeCameraObject()
        {
            var brain = GetBrain();
            if (brain == null) return null;

            var vcam = CinemachineService.GetActiveVirtualCamera(brain);
            if (vcam == null) return null;

            return CinemachineService.GetFreeCameraObjectTransform(vcam);
        }

        /// <summary>
        /// 让游戏自己的拖拽/键盘平移跟着地图角度走(默认开启, blockPanWhileRotate 关闭时也仍然换算方向)。
        ///
        /// 背景: 游戏的 <c>FreeCameraObject.HandleMoveCamera</c> / <c>HandleControlCamera</c>
        /// 用的是自由相机对象的<b>世界</b> forward/right(Unity 默认把 ←/→ 也映射进 Horizontal 轴),
        /// 而画面已经被我们绕观察点转了 <c>yawDelta</c>。相机实际是沿世界轴平移、朝向却转了,
        /// 所以旋转后拖拽/键盘平移会歪掉同样的角度(+/- 方向)。
        ///
        /// 处理: 每帧最后取"本帧自由相机对象移动了多少", 把这个位移绕基准点转 <c>yawDelta</c> 度后写回:
        /// 世界位移 = R(yawDelta) × 游戏本来要走的位移, 于是画面上仍是想拖的方向。
        ///   - 按住旋转键(blockPanWhileRotate=true)时, 这一帧位移整个抵掉, 左右键只旋转地图;
        ///   - 没有旋转时完全不碰自由相机;
        ///   - 游戏主动搬运相机(换玩家/演出摆放, 位移很大)时只重新取基准, 不换算。
        ///
        /// 只对自由相机对象生效; 角色相机/演出相机不做任何位置改写。
        /// </summary>
        private void UpdateRotatePan()
        {
            // 没有旋转、也没在按旋转键: 不需要干预自由相机
            if (!HasRotation && !_rotateHeldNow)
            {
                _panTracking = false;
                return;
            }

            var target = GetFreeCameraObject();
            if (target == null)
            {
                // 不是玩家在操作自由相机(角色相机/演出): 游戏也不会用方向键推它
                _panTarget = null;
                _panTracking = false;
                return;
            }

            Vector3 position = TransformService.GetPosition(target);
            if (!_panTracking || !ReferenceEquals(target, _panTarget))
            {
                // 第一帧(或刚换了目标): 以当前位置为基准, 本帧不换算
                _panTarget = target;
                _panSnapshot = position;
                _panTracking = true;
                return;
            }

            float moved = HorizontalDistance(position, _panSnapshot);
            if (moved > MaxPanSuppressDistance)
            {
                // 游戏主动把自由相机搬到了别处(换玩家/演出摆放): 不换算, 重新取基准
                _panSnapshot = position;
                return;
            }

            float newX = position.x;
            float newZ = position.z;

            if (_rotateHeldNow && _blockPanWhileRotate)
            {
                // 按住旋转键: 抵掉游戏自己的方向键平移, 让左右键只旋转地图
                newX = _panSnapshot.x;
                newZ = _panSnapshot.z;
            }
            else if (HasRotation && moved > 0.000001f)
            {
                // 按当前地图角度换算这一帧的位移: 旋转后拖拽/键盘平移方向仍与画面对应
                FreeCameraMath.RotateAroundVertical(position.x, position.z,
                    _panSnapshot.x, _panSnapshot.z, NormalizeAngle(_yawDelta), out newX, out newZ);
            }

            if (Math.Abs(newX - position.x) > 0.0001f || Math.Abs(newZ - position.z) > 0.0001f)
                TransformService.SetPosition(target, new Vector3(newX, position.y, newZ));

            _panSnapshot = new Vector3(newX, position.y, newZ);
        }

        /// <summary>水平(XZ)距离。</summary>
        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        // =====================================================================
        // 相机事件
        // =====================================================================

        private void OnMainCameraLost()
        {
            ResetBrainCache();
            if (!_active) return;
            Log.Warn("自由相机: 主相机消失, 结束接管(下一帧由游戏自己接回相机)");
            ResetRuntime();
        }

        private void OnMainCameraChanged(Camera camera)
        {
            ResetBrainCache();
            if (!_active) return;
            Log.Info("自由相机: 主相机更换, 结束接管并清零偏移/旋转");
            ResetRuntime();
        }

        /// <summary>主相机换了: 缓存的 Brain/活动 vcam 解析必须重新做。</summary>
        private void ResetBrainCache()
        {
            _brain = null;
            _brainOwner = null;
            _brainProbeCooldown = 0;
        }

        // =====================================================================
        // 自动跟随
        // =====================================================================

        /// <summary>
        /// 维护"关闭自动跟随": 把角色相机的 Follow 指向固定锚点。
        ///
        /// 关键行为(2.4.2 起):
        ///   - 锚点在每个场景里只取一次初始位姿(第一次进入角色相机时), 之后角色走路、
        ///     换当前玩家、换角色相机都不再移动它 —— 否则"切换角色"时镜头会跳到新角色;
        ///   - 混合期间 <c>ActiveVirtualCamera</c> 可能还是上一台相机, 所以在换相机/
        ///     开始混合时扫描全部 vcam, 把所有角色相机都提前改成锚点, 这样切入的新
        ///     角色相机不会先跟随角色再被拽回;
        ///   - 回合开始/刷怪等流程会直接搬动自由相机对象, 改 Follow 挡不住; 因此
        ///     <see cref="UpdateFollowCameraLock"/> 每帧在 PostLateUpdate 把主相机位姿
        ///     锁回固定值, 只有玩家主动拖拽/按键平移自由相机时才解除那一帧的锁定。
        ///
        /// 为什么改 vcam 的 Follow 而不是每帧钉住主相机:
        ///   - 本作角色相机由 <c>Core.Camera.CharacterCamera</c> 创建, 它只做一件事
        ///     —— 把 vcam 的 <c>m_Follow</c> 设成角色 transform。角色走路时镜头跟着走
        ///     就是 Cinemachine 的 Follow 在起作用;
        ///   - 把 Follow 换成固定在原地的锚点, 角色移动就不再带动镜头, 而自由相机的
        ///     拖拽平移(FreeCameraObject 自己的 vcam)、切玩家/演出运镜(别的 vcam 或
        ///     BattleEffect.SceneCamLock)完全不受影响;
        ///   - 反过来, 如果在 LateUpdate 里直接钉住主相机, 会把玩家自己的拖拽平移和
        ///     演出运镜一起冻住。
        /// </summary>
        private void MaintainAutoFollow()
        {
            if (_autoFollow) return;

            // 输入每帧只采集一次(鼠标位置差分要在同一帧内保持一致);
            // OnLateUpdate 的锁定逻辑直接复用这个结果。
            _playerMovingFreeCamera = IsPlayerMovingFreeCamera();

            var brain = GetBrain();
            if (brain == null) return;

            var main = CameraService.GetMainCamera();
            if (!UnityObject.IsAlive(main))
            {
                _hasLastGameCameraPosition = false;
                return;
            }

            bool blending = CinemachineService.IsBlending(brain);
            bool blendStarted = blending && !_wasBlending;
            _wasBlending = blending;

            var vcam = CinemachineService.GetActiveVirtualCamera(brain);
            int id = (vcam != null) ? UnityObject.GetInstanceId(vcam as UnityEngine.Object) : 0;
            bool activeChanged = id != _lastFollowVcamId;
            _lastFollowVcamId = id;

            bool activeIsCharacter = false;
            if (id != 0)
            {
                activeIsCharacter = FindFollowOverride(id) != null;
                if (!activeIsCharacter && activeChanged)
                    activeIsCharacter = TryAdoptCharacterVcam(vcam);
            }

            // 换相机/开始混合时, 把所有角色相机提前锁到锚点(包括正在切入的那台)。
            if (activeChanged || blendStarted) AnchorAllCharacterVcams();

            if (activeIsCharacter)
            {
                FollowOverride info = FindFollowOverride(id);
                if (info != null)
                {
                    if (!_hasFollowAnchorPose) SetFollowAnchorPose(info.OriginalFollow);
                    if (_hasFollowAnchorPose) EnsureVcamUsesAnchor(vcam, info);

                    // 稳定状态下记录游戏相机位置; 混合期间保留旧值, 这样从角色相机
                    // 切到自由相机时能把整段混合位移补进锚点。
                    if (!blending)
                    {
                        _lastGameCameraPosition = GetGameCameraPosition(main);
                        _hasLastGameCameraPosition = true;

                        // 第一次稳定到角色相机后锁定主相机位姿。之后角色移动、换角色、
                        // 回合开始、刷怪、演出运镜都不会再移动画面; 只有玩家拖拽/按键
                        // 平移自由相机时才更新锁定位置。
                        if (!_hasCameraLock)
                        {
                            _lockedCameraPosition = _lastGameCameraPosition;
                            _lockedCameraEuler = CameraService.GetEulerAngles(main);
                            _lockedMainCamera = main;
                            _hasCameraLock = true;
                            Log.Info("自动跟随: 已锁定主相机位姿 (" +
                                      F(_lockedCameraPosition.x) + ", " + F(_lockedCameraPosition.y) + ", " +
                                      F(_lockedCameraPosition.z) + "); 游戏自动运镜不会再移动画面");
                            // 找出本作的 PK 演出相机(开大/战斗切入时游戏会禁用主相机、改用这台)
                            RefreshExtraCameras();
                        }
                        else if (_lockedMainCamera == null)
                        {
                            _lockedMainCamera = main;
                        }
                    }
                }
                return;
            }

            // 不是角色相机。只有玩家自己拖拽/按键移动自由相机时才把锚点一起带走;
            // 游戏事件/演出用自由相机运镜时不改变固定镜头。
            Vector3 gamePosition = GetGameCameraPosition(main);
            bool playerPanning = CinemachineService.IsFollowingFreeCamera(vcam) && _playerMovingFreeCamera;
            if (!playerPanning || !_hasFollowAnchorPose || !_hasLastGameCameraPosition)
            {
                _lastGameCameraPosition = gamePosition;
                _hasLastGameCameraPosition = true;
                return;
            }

            if (IsPlaceholderCamera(gamePosition, CameraService.GetEulerAngles(main)))
            {
                _lastGameCameraPosition = gamePosition;
                return;
            }

            Vector3 delta = gamePosition - _lastGameCameraPosition;
            if (delta.sqrMagnitude > 0.000001f)
            {
                var anchor = GetOrCreateFollowAnchor();
                if (anchor != null)
                    TransformService.SetPosition(anchor, TransformService.GetPosition(anchor) + delta);
            }
            _lastGameCameraPosition = gamePosition;
        }

        /// <summary>活动 vcam 是新的角色相机: 登记原始 Follow, 首次落锚点后改写。</summary>
        private bool TryAdoptCharacterVcam(object vcam)
        {
            int id = UnityObject.GetInstanceId(vcam as UnityEngine.Object);
            if (id == 0) return false;

            FollowOverride info = FindFollowOverride(id);
            if (info == null)
            {
                Transform follow = CinemachineService.GetFollow(vcam);
                if (!CinemachineService.IsCharacterTransform(follow)) return false;

                info = new FollowOverride
                {
                    Vcam = vcam,
                    VcamId = id,
                    OriginalFollow = follow
                };
                _followOverrides.Add(info);
            }

            if (!_hasFollowAnchorPose) SetFollowAnchorPose(info.OriginalFollow);
            if (_hasFollowAnchorPose) EnsureVcamUsesAnchor(vcam, info);
            return true;
        }

        /// <summary>把场景里所有角色相机的 Follow 提前改成锚点(混合期间切入的相机也不会先跟随角色)。</summary>
        private void AnchorAllCharacterVcams()
        {
            if (!_hasFollowAnchorPose) return;

            var vcams = CinemachineService.GetVirtualCameras(true);
            for (int i = 0; i < vcams.Count; i++)
            {
                var vcam = vcams[i];
                if (!UnityObject.IsAlive(vcam as UnityEngine.Object)) continue;

                int id = UnityObject.GetInstanceId(vcam as UnityEngine.Object);
                if (id == 0) continue;

                FollowOverride info = FindFollowOverride(id);
                if (info == null)
                {
                    Transform follow = CinemachineService.GetFollow(vcam);
                    if (!CinemachineService.IsCharacterTransform(follow)) continue;

                    info = new FollowOverride
                    {
                        Vcam = vcam,
                        VcamId = id,
                        OriginalFollow = follow
                    };
                    _followOverrides.Add(info);
                }

                EnsureVcamUsesAnchor(vcam, info);
            }
        }

        /// <summary>取第一个角色相机的位置作为固定镜头位置(每个场景只做一次)。</summary>
        private void SetFollowAnchorPose(Transform source)
        {
            if (!UnityObject.IsAlive(source)) return;

            Transform anchor = GetOrCreateFollowAnchor();
            if (anchor == null) return;

            Vector3 position = TransformService.GetPosition(source);
            TransformService.SetPosition(anchor, position);
            TransformService.SetRotation(anchor, TransformService.GetRotation(source));
            _hasFollowAnchorPose = true;

            Log.Info("自动跟随: 已关闭, 镜头固定在当前角色位置 (" +
                     F(position.x) + ", " + F(position.y) + ", " + F(position.z) + ")" +
                     "; 之后角色移动、切换角色都不会再移动镜头");

            // 第一次落锚点后立刻把场景里已有的角色相机全部锁上, 避免后续切换时
            // 新相机还先跟着角色走一帧。
            AnchorAllCharacterVcams();
        }

        /// <summary>确保一台角色相机的 Follow/LookAt 指向锚点; 不改变锚点位置。</summary>
        private void EnsureVcamUsesAnchor(object vcam, FollowOverride info)
        {
            if (info == null) return;

            Transform anchor = GetOrCreateFollowAnchor();
            if (anchor == null) return;

            // LookAt 也指着这个角色时一并冻结, 否则镜头还会转向角色
            if (!info.LookAtOverridden)
            {
                Transform lookAt = CinemachineService.GetLookAt(vcam);
                if (CinemachineService.IsCharacterTransform(lookAt))
                {
                    info.OriginalLookAt = lookAt;
                    info.LookAtOverridden = true;
                }
            }

            Transform current = CinemachineService.GetFollow(vcam);
            if (current == anchor)
            {
                if (info.LookAtOverridden && CinemachineService.GetLookAt(vcam) != anchor)
                    CinemachineService.SetLookAt(vcam, anchor);
                return;
            }

            // 游戏把同一台 vcam 重新绑到了另一个角色: 只更新还原用的原始目标, 不移动锚点
            if (UnityObject.IsAlive(current) && current != info.OriginalFollow &&
                CinemachineService.IsCharacterTransform(current))
            {
                info.OriginalFollow = current;
            }

            if (!CinemachineService.SetFollow(vcam, anchor))
            {
                if (!_followWarned)
                {
                    _followWarned = true;
                    Log.Warn("自动跟随: 无法改写角色相机的 Follow(当前 vcam 不支持), 本次启动内不再重试");
                }
                return;
            }

            if (info.LookAtOverridden) CinemachineService.SetLookAt(vcam, anchor);
        }

        /// <summary>游戏相机位置; 滚轮缩放期间用基准位姿, 排除 mod 自己写的偏移。</summary>
        private Vector3 GetGameCameraPosition(Camera main)
        {
            if (_active && _hasBasePose) return _basePosition;
            return CameraService.GetPosition(main);
        }

        /// <summary>
        /// 每帧把主相机锁回固定视角(autoFollow=false)。返回是否已锁住。
        ///
        /// 只做两件事:
        ///   - 玩家正在主动拖拽/按键平移自由相机时, 不覆盖相机, 而是把锁定位置更新到
        ///     当前画面(玩家操作完就停在这里);
        ///   - 其他时候(角色移动、换角色、回合开始、刷怪、演出运镜)把主相机的位置和
        ///     朝向都写回锁定值, 所以画面完全不动。
        /// 菜单/过渡占位相机出现时解除锁定, 交回给游戏(下次进入角色相机再锁)。
        /// </summary>
        private bool UpdateFollowCameraLock()
        {
            if (_autoFollow || !_hasCameraLock) return false;

            // 演出/开大时游戏会禁用棋盘主相机、启用独立的 PKMainCamera;
            // 这台相机也要冻结在开场机位, 否则开大运镜照样会动。
            UpdateExtraCameraLocks();

            // 固定锁在第一次捕获的棋盘主相机上。演出期间 Camera.main 可能切到 PK 相机,
            // 不能用 CameraService.GetMainCamera() 的当前结果去写棋盘视角。
            var camera = _lockedMainCamera;
            if (!UnityObject.IsAlive(camera)) camera = CameraService.GetMainCamera();
            if (!UnityObject.IsAlive(camera)) return false;
            if (!ReferenceEquals(camera, _lockedMainCamera))
            {
                _lockedMainCamera = camera;
                RefreshExtraCameras();
            }

            Vector3 currentPosition = CameraService.GetPosition(camera);
            Vector3 currentEuler = CameraService.GetEulerAngles(camera);

            // 注意: 这里不要因为"占位相机"而释放锁定。回合开始/刷怪/演出期间游戏会把
            // 主相机临时挪到 (0,0,0) 或高处(或者禁用), 释放锁定就会让镜头跟着游戏跑到
            // 新位置; 切场景时 OnSceneLoaded 会统一 ResetFollowOverride。

            // 玩家主动平移自由相机: 让游戏相机正常移动, 并把固定视角更新到当前画面。
            // 相机里可能还留着本 mod 写的缩放/旋转, 不能直接把它当成"锁定值", 否则下一次锁定
            // 会把这些偏移再叠加一次(旋转会越转越多)。此时用上一帧识别到的游戏位姿。
            if (IsPlayerPanningFreeCamera())
            {
                if (_active && _hasBasePose)
                {
                    _lockedCameraPosition = _basePosition;
                    _lockedCameraEuler = _baseEuler;
                }
                else
                {
                    _lockedCameraPosition = currentPosition;
                    _lockedCameraEuler = currentEuler;
                }
                return false;
            }

            _basePosition = _lockedCameraPosition;
            _baseEuler = _lockedCameraEuler;
            _hasBasePose = true;

            // 朝向每帧都写回锁定值(接管时也写: 旋转会在后面再叠加 yaw);
            // 位置只在没接管时写 —— 接管时的位置由缩放/旋转代码按 _basePosition 叠加。
            CameraService.SetEulerAngles(camera, _lockedCameraEuler);
            if (!_active) CameraService.SetPosition(camera, _lockedCameraPosition);
            return true;
        }

        /// <summary>
        /// 重新收集带 CinemachineBrain 的额外相机(本作是 PKMainCamera)。
        /// 主相机由 <see cref="UpdateFollowCameraLock"/> 单独锁定, 这里跳过。
        /// </summary>
        private void RefreshExtraCameras()
        {
            _extraCameraLocks.Clear();

            var main = _lockedMainCamera;
            if (!UnityObject.IsAlive(main)) main = CameraService.GetMainCamera();

            var cameras = CameraService.GetAllCameras(true);
            for (int i = 0; i < cameras.Length; i++)
            {
                var cam = cameras[i];
                if (!UnityObject.IsAlive(cam)) continue;
                if (UnityObject.IsAlive(main) && ReferenceEquals(cam, main)) continue;
                if (!HasCinemachineBrain(cam)) continue;

                bool duplicate = false;
                for (int j = 0; j < _extraCameraLocks.Count; j++)
                {
                    if (ReferenceEquals(_extraCameraLocks[j].Camera, cam)) { duplicate = true; break; }
                }
                if (duplicate) continue;

                _extraCameraLocks.Add(new ExtraCameraLock { Camera = cam, Delay = 1 });
            }

            Log.Debug("自动跟随: 额外演出相机 " + _extraCameraLocks.Count + " 台");
        }

        /// <summary>
        /// 相机自身或最近几层父节点上是否有 CinemachineBrain。
        /// PK 相机可能挂在 PKCameraManager 的子节点上, 所以不能只看相机自己的 GameObject。
        /// </summary>
        private static bool HasCinemachineBrain(Camera camera)
        {
            if (!UnityObject.IsAlive(camera)) return false;

            var go = UnityObject.GetGameObject(camera);
            if (go != null && CinemachineService.FindBrainOn(go) != null) return true;

            Transform parent = TransformService.GetParent(camera.transform);
            for (int i = 0; i < 4 && parent != null; i++)
            {
                if (CinemachineService.FindBrainOn(parent.gameObject) != null) return true;
                parent = TransformService.GetParent(parent);
            }
            return false;
        }

        /// <summary>
        /// 冻结额外演出相机(PKMainCamera)。开大/战斗切入时游戏会禁用主相机、改用这台相机
        /// 播放运镜; 把它锁在启用时的第一帧位姿和镜头参数上, 画面就不会跟着运镜跑。
        /// </summary>
        private void UpdateExtraCameraLocks()
        {
            // 清理已经销毁的相机; 列表空了再按冷却重新扫描(相机可能比锁定晚创建)
            for (int i = _extraCameraLocks.Count - 1; i >= 0; i--)
            {
                if (!UnityObject.IsAlive(_extraCameraLocks[i].Camera)) _extraCameraLocks.RemoveAt(i);
            }

            if (_extraCameraLocks.Count == 0)
            {
                if (_extraCameraScanCooldown > 0)
                {
                    _extraCameraScanCooldown--;
                    return;
                }
                _extraCameraScanCooldown = 30;
                RefreshExtraCameras();
                if (_extraCameraLocks.Count == 0) return;
            }

            for (int i = 0; i < _extraCameraLocks.Count; i++)
            {
                var state = _extraCameraLocks[i];
                var cam = state.Camera;
                if (!UnityObject.IsAlive(cam)) continue;

                bool enabled = CameraService.GetEnabled(cam) && UnityObject.IsActiveInHierarchy(cam);
                if (!enabled)
                {
                    state.HasPose = false;
                    state.Delay = 1;
                    continue;
                }

                if (!state.HasPose)
                {
                    // 等一帧让 CinemachineBrain 把相机写到本次演出的第一个机位, 再捕获
                    if (state.Delay > 0)
                    {
                        state.Delay--;
                        continue;
                    }

                    // 启用后的第一帧: 记下游戏给的初始演出机位/镜头参数, 之后不再让它动
                    state.Position = CameraService.GetPosition(cam);
                    state.Euler = CameraService.GetEulerAngles(cam);
                    state.FieldOfView = CameraService.GetFieldOfView(cam);
                    state.Orthographic = CameraService.GetOrthographic(cam);
                    state.OrthographicSize = CameraService.GetOrthographicSize(cam);
                    state.HasPose = true;
                    Log.Info("自动跟随: 已冻结演出相机 " + UnityObject.GetName(cam) + " 于开场机位");
                    continue;
                }

                CameraService.SetPosition(cam, state.Position);
                CameraService.SetEulerAngles(cam, state.Euler);
                CameraService.SetFieldOfView(cam, state.FieldOfView);
                CameraService.SetOrthographic(cam, state.Orthographic);
                CameraService.SetOrthographicSize(cam, state.OrthographicSize);
            }
        }

        /// <summary>玩家是否正在主动平移自由相机(此时允许画面移动)。</summary>
        private bool IsPlayerPanningFreeCamera()
        {
            if (!_playerMovingFreeCamera) return false;

            var brain = GetBrain();
            if (brain == null) return false;
            var vcam = CinemachineService.GetActiveVirtualCamera(brain);
            return CinemachineService.IsFollowingFreeCamera(vcam);
        }

        /// <summary>
        /// 玩家是否正在主动移动自由相机(鼠标左键拖拽或方向键)。
        /// 只有这种输入才允许移动固定镜头; 游戏事件用自由相机运镜不算。
        ///
        /// 注意: ←/→ 会被游戏当成 "Horizontal" 轴(键盘平移)。按住旋转键时它们只负责旋转地图,
        /// 而且那点平移会被 <see cref="UpdateRotatePan"/> 拉回去, 所以不能算"玩家在平移" ——
        /// 否则锁定镜头会跟着漂、锚点也会被带走。同一帧按住 W/S 仍然算平移。
        /// </summary>
        private bool IsPlayerMovingFreeCamera()
        {
            try
            {
                if (InputService.IsMouseButtonHeld(0))
                {
                    Vector2 position;
                    if (InputService.TryGetMousePosition(out position))
                    {
                        if (_hasLastMousePosition)
                        {
                            Vector2 delta = position - _lastMousePosition;
                            _lastMousePosition = position;
                            // 鼠标按住且真的在拖: 点一下 UI(位置不变)不算移动相机
                            if (delta.sqrMagnitude > 0.25f) return true;
                        }
                        else
                        {
                            _lastMousePosition = position;
                            _hasLastMousePosition = true;
                        }
                    }
                    else
                    {
                        // 取不到光标位置(光标锁定/后端限制)时退回"按住就算拖拽"
                        return true;
                    }
                }
                else
                {
                    _hasLastMousePosition = false;
                }

                float horizontal = InputService.GetAxis("Horizontal");
                if (_rotateHeldNow) horizontal = 0f;   // ←/→ 正在旋转地图, 不算平移
                float vertical = InputService.GetAxis("Vertical");

                // 留死区: 手柄漂移/极小轴值不应该把固定镜头解除锁定
                return horizontal * horizontal + vertical * vertical > 0.04f;
            }
            catch { return false; }
        }

        /// <summary>缓存的 CinemachineBrain(FindBrain 会全场景搜索)。</summary>
        private object GetBrain()
        {
            var main = CameraService.GetMainCamera();
            var cached = _brain as UnityEngine.Object;
            if (cached != null && UnityObject.IsAlive(cached) && ReferenceEquals(_brainOwner, main)) return _brain;

            // 菜单/过场里可能还没有 Brain: 最多每秒重找一次, 不每帧全场景搜索。
            if (_brainProbeCooldown > 0)
            {
                _brainProbeCooldown--;
                return null;
            }
            _brainProbeCooldown = 60;
            _brainOwner = main;

            // 本作可能同时有主相机与 PK 演出相机两个 Brain; 优先取主相机上的那个。
            _brain = CinemachineService.FindBrainOn(main);
            if (_brain == null) _brain = CinemachineService.FindBrain();
            return _brain;
        }

        /// <summary>取固定跟随锚点(不存在或被场景销毁时重建)。</summary>
        private Transform GetOrCreateFollowAnchor()
        {
            if (_followAnchor != null && UnityObject.IsAlive(_followAnchor))
                return _followAnchor.transform;

            var go = GameObjectUtil.Create("CesiumFreeCameraFollowAnchor");
            if (go == null) return null;
            _followAnchor = go;
            Log.Debug("自动跟随: 已创建固定跟随锚点");
            return go.transform;
        }

        private FollowOverride FindFollowOverride(int vcamId)
        {
            for (int i = 0; i < _followOverrides.Count; i++)
            {
                var info = _followOverrides[i];
                if (info.VcamId != vcamId) continue;
                if (!UnityObject.IsAlive(info.Vcam as UnityEngine.Object)) continue;
                return info;
            }
            return null;
        }

        /// <summary>还原一台 vcam 的 Follow/LookAt(只在对象仍存活时)。</summary>
        private void RestoreFollow(FollowOverride info)
        {
            if (info == null) return;
            try
            {
                if (!UnityObject.IsAlive(info.Vcam as UnityEngine.Object)) return;
                if (UnityObject.IsAlive(info.OriginalFollow))
                    CinemachineService.SetFollow(info.Vcam, info.OriginalFollow);
                if (info.LookAtOverridden && UnityObject.IsAlive(info.OriginalLookAt))
                    CinemachineService.SetLookAt(info.Vcam, info.OriginalLookAt);
            }
            catch (Exception e)
            {
                Log.ReportCrash("RestoreFollow", e);
            }
        }

        /// <summary>还原全部被改写的相机并丢弃锚点(卸载 / 切场景时用)。</summary>
        private void ResetFollowOverride()
        {
            for (int i = 0; i < _followOverrides.Count; i++) RestoreFollow(_followOverrides[i]);
            _followOverrides.Clear();
            _lastFollowVcamId = 0;
            _hasFollowAnchorPose = false;
            _hasLastGameCameraPosition = false;
            _lastGameCameraPosition = Vector3.zero;
            _wasBlending = false;
            _hasCameraLock = false;
            _lockedCameraPosition = Vector3.zero;
            _lockedCameraEuler = Vector3.zero;
            _lockedMainCamera = null;
            _extraCameraLocks.Clear();
            _extraCameraScanCooldown = 0;
            _playerMovingFreeCamera = false;
            _hasLastMousePosition = false;
            _brain = null;
            _brainOwner = null;
            _brainProbeCooldown = 0;
            _followWarned = false;

            if (UnityObject.IsAlive(_followAnchor)) UnityObject.SafeDestroy(_followAnchor);
            _followAnchor = null;
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

        private void LoadConfig(bool isInitial = false)
        {
            if (_config == null) return;

            // 旧键迁移只在初始化时做; 热重载时不再改磁盘, 免得和外部编辑器互相覆盖。
            string legacyToggle = isInitial ? _config.GetString("toggleKey", null) : null;
            bool legacyRemoved = false;
            if (isInitial)
            {
                foreach (string key in LegacyKeys)
                {
                    if (_config.Remove(key)) legacyRemoved = true;
                }
            }

            _autoFollow = _config.GetBool("autoFollow", _autoFollow);
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
            if (isInitial && _config.Remove("toggleKey")) legacyRemoved = true;

            // 旋转地图: ←/→ 默认; 键名写 "None" 可单独关掉某个方向。
            // 键名以 Key 结尾(rotateLeftKey)是配置页"按键捕获"控件的约定; 早期 2.6.0 测试配置
            // 用过 rotateKeyLeft/rotateKeyRight, 这里继承旧值并删掉旧键。
            string rotateLeft = _config.GetString("rotateLeftKey", null);
            if (string.IsNullOrEmpty(rotateLeft) && isInitial) rotateLeft = _config.GetString("rotateKeyLeft", null);
            _rotateLeftKey = ParseKey(rotateLeft, _rotateLeftKey);
            string rotateRight = _config.GetString("rotateRightKey", null);
            if (string.IsNullOrEmpty(rotateRight) && isInitial) rotateRight = _config.GetString("rotateKeyRight", null);
            _rotateRightKey = ParseKey(rotateRight, _rotateRightKey);
            if (isInitial)
            {
                if (_config.Remove("rotateKeyLeft")) legacyRemoved = true;
                if (_config.Remove("rotateKeyRight")) legacyRemoved = true;
            }
            _rotateSpeed = _config.GetFloat("rotateSpeed", _rotateSpeed);
            _rotateStep = _config.GetFloat("rotateStep", _rotateStep);
            _invertRotate = _config.GetBool("invertRotate", _invertRotate);
            _blockPanWhileRotate = _config.GetBool("blockPanWhileRotate", _blockPanWhileRotate);

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

            // 旋转速度 0 = 不连续转(只按点按步进); 非法值退回默认, 上限防止"一按转好几圈"
            if (float.IsNaN(_rotateSpeed) || float.IsInfinity(_rotateSpeed) || _rotateSpeed < 0f)
                _rotateSpeed = FreeCameraMath.DefaultRotateSpeed;
            _rotateSpeed = Math.Min(_rotateSpeed, FreeCameraMath.MaxRotateSpeed);
            if (float.IsNaN(_rotateStep) || float.IsInfinity(_rotateStep) || _rotateStep < 0f)
                _rotateStep = FreeCameraMath.DefaultRotateStep;
            _rotateStep = Math.Min(_rotateStep, 180f);

            if (isInitial) WriteDefaults(legacyRemoved);
        }

        /// <summary>把当前(已兜底的)配置写回文件: 缺键或清掉了旧键时落盘一次, 让用户能直接改。</summary>
        private void WriteDefaults(bool force)
        {
            bool docsChanged = FreeCameraConfigDocs.Apply(_config);
            if (!force && _config.Has("autoFollow") && _config.Has("zoomStep") && _config.Has("restoreKey") &&
                _config.Has("rotateLeftKey") && _config.Has("rotateSpeed") && _config.Has("rotateStep") &&
                _config.Has("invertRotate"))
            {
                if (docsChanged)
                {
                    _config.SaveIfDirty();
                    _configFixed = true;
                }
                return;
            }

            _config.Set("autoFollow", _autoFollow);
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
            _config.Set("rotateLeftKey", _rotateLeftKey.ToString());
            _config.Set("rotateRightKey", _rotateRightKey.ToString());
            _config.Set("rotateSpeed", _rotateSpeed);
            _config.Set("rotateStep", _rotateStep);
            _config.Set("invertRotate", _invertRotate);
            _config.Set("blockPanWhileRotate", _blockPanWhileRotate);
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
