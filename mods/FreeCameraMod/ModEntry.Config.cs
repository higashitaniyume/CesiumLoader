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
}
