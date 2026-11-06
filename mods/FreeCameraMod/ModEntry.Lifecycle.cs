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

                // ---- 点击怪物头像查信息：平移与缩放不占用输入时才记 ----
                TrackInspectClick();

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

                // 点击怪物头像触发（跟随目标已切到新角色）：丢掉缩放/旋转与固定锚点，
                // 交回游戏走正常切换；下一帧按新怪物的机位重新落锚/锁定。
                if (_focusRestoreRequested)
                {
                    _focusRestoreRequested = false;
                    _pendingInspectClick = false;
                    if (_active)
                    {
                        var camFR = (_driven != null && UnityObject.IsAlive(_driven))
                            ? _driven : CameraService.GetMainCamera();
                        if (UnityObject.IsAlive(camFR)) RestoreBasePose(camFR);
                        ResetRuntime();
                    }
                    if (!_autoFollow) ResetFollowOverride();
                    Log.Info("自由相机: 点击怪物头像切换镜头，已还原视角（镜头聚焦）");
                    Notify("自由相机: 已恢复视角以查看怪物");
                    return;
                }

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
                Vector3 baseBefore = _basePosition;
                bool hadBaseBefore = _hasBasePose;
                bool gameWroteThisFrame = false;
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
                        gameWroteThisFrame = true;
                    }
                }

                // 1b) 点击怪物头像：游戏镜头跳变（切到怪物）且开关开启时，
                //     还原缩放/旋转并交回游戏；没有点击的自动运镜不受影响。
                if (_restoreOnMonsterFocus && !cameraLocked && IsInspectClickFresh() &&
                    hadBaseBefore && gameWroteThisFrame && Distance(gamePosition, baseBefore) > 5f)
                {
                    _pendingInspectClick = false;
                    float restoredOffset = _offset;
                    float restoredYaw = _yawDelta;
                    RestoreBasePose(camera);
                    ResetRuntime();
                    if (!_autoFollow) ResetFollowOverride();
                    Log.Info("自由相机: 点击怪物头像切换镜头，已还原视角（偏移 " +
                             F(restoredOffset) + "m -> 0, 旋转 " + F(restoredYaw) + "° -> 0）");
                    Notify("自由相机: 已恢复视角以查看怪物");
                    return;
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

        /// <summary>
        /// 记住左键一下（按下抬起位移≤12px 且用时≤0.8s），等待游戏自己切镜头。
        /// 拖拽平移位移大、自动运镜无点击，都不会置位，避免把回合切换误当点头像。
        /// 只在复原开关打开时记录；UI 阻塞时不记（由调用方保证已过 ShouldBlock 检查）。
        /// </summary>
        private void TrackInspectClick()
        {
            if (!_restoreOnMonsterFocus)
            {
                _mouseDownActive = false;
                _pendingInspectClick = false;
                return;
            }
            if (!IsInspectClickFresh() && _pendingInspectClick)
            {
                // 1.5s 内游戏还没切镜头，视为这次点击不是点头像，丢掉
                _pendingInspectClick = false;
            }
            try
            {
                if (InputService.IsMouseButtonPressed(0))
                {
                    Vector2 pos;
                    if (InputService.TryGetMousePosition(out pos))
                    {
                        _mouseDownPos = pos;
                        _mouseDownTime = DateTime.UtcNow;
                        _mouseDownActive = true;
                    }
                }
                if (_mouseDownActive && InputService.IsMouseButtonReleased(0))
                {
                    Vector2 pos2;
                    if (InputService.TryGetMousePosition(out pos2))
                    {
                        float disp = (pos2 - _mouseDownPos).magnitude;
                        double dur = (DateTime.UtcNow - _mouseDownTime).TotalSeconds;
                        if (disp <= 12f && dur >= 0d && dur <= 0.8d)
                        {
                            _pendingInspectClick = true;
                            _inspectClickTime = DateTime.UtcNow;
                            Log.Debug("自由相机: 检测到点击，等待镜头切换（查怪）");
                        }
                    }
                    _mouseDownActive = false;
                }
                if (_mouseDownActive && !InputService.IsMouseButtonHeld(0))
                    _mouseDownActive = false;
            }
            catch { _mouseDownActive = false; }
        }

        private bool IsInspectClickFresh()
        {
            if (!_pendingInspectClick) return false;
            double age = (DateTime.UtcNow - _inspectClickTime).TotalSeconds;
            return age >= 0d && age <= 1.5d;
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

    }
}
