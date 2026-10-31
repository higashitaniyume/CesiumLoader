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

    }
}
