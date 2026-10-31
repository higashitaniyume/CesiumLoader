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
    }
}
