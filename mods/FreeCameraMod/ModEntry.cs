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
    public sealed partial class ZoomCameraController : ModBase
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
