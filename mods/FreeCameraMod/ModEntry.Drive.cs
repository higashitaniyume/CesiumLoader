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
    public sealed partial class ZoomCameraController : ModBase
    {
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

    }
}
