using System;
using UnityEngine;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 相机完整状态快照。
    ///
    /// 用途: 自由相机/任何相机接管方案在进入时保存、退出时<b>完整</b>还原,
    /// 保证 mod 不会永久改坏游戏相机。
    ///
    /// 设计要点(为什么不用 Vector3/Quaternion 字段直接存储):
    ///  - 位置/旋转/颜色以<b>标量字段</b>存储, 使快照与 JSON 序列化完全不依赖
    ///    UnityEngine 结构体 —— 这样序列化/还原逻辑可以脱离游戏做单元测试,
    ///    格式也更适合跨工具搬运。
    ///  - Unity 类型的便捷访问通过 <see cref="Position"/> / <see cref="Rotation"/> 等
    ///    属性提供(标了 <see cref="CesiumJsonIgnoreAttribute"/> 以免重复序列化)。
    /// </summary>
    [Serializable]
    public struct CameraState
    {
        // ---------------------------------------------------------------------
        // 纯数据(全部为 BCL 类型, 可离线测试)
        // ---------------------------------------------------------------------

        /// <summary>快照是否有效。</summary>
        public bool Valid;

        /// <summary>相机名(仅诊断用)。</summary>
        public string Name;

        /// <summary>世界坐标 X。</summary>
        public float PositionX;

        /// <summary>世界坐标 Y。</summary>
        public float PositionY;

        /// <summary>世界坐标 Z。</summary>
        public float PositionZ;

        /// <summary>世界旋转四元数 X。</summary>
        public float RotationX;

        /// <summary>世界旋转四元数 Y。</summary>
        public float RotationY;

        /// <summary>世界旋转四元数 Z。</summary>
        public float RotationZ;

        /// <summary>世界旋转四元数 W。</summary>
        public float RotationW;

        /// <summary>世界欧拉角 X(便于人工阅读/编辑)。</summary>
        public float EulerX;

        /// <summary>世界欧拉角 Y。</summary>
        public float EulerY;

        /// <summary>世界欧拉角 Z。</summary>
        public float EulerZ;

        /// <summary>视场角。</summary>
        public float FieldOfView;

        /// <summary>近裁剪面。</summary>
        public float NearClipPlane;

        /// <summary>远裁剪面。</summary>
        public float FarClipPlane;

        /// <summary>正交尺寸。</summary>
        public float OrthographicSize;

        /// <summary>是否正交投影。</summary>
        public bool Orthographic;

        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>渲染顺序。</summary>
        public float Depth;

        /// <summary>剔除遮罩。</summary>
        public int CullingMask;

        /// <summary>清屏标志(枚举底层值)。</summary>
        public int ClearFlags;

        /// <summary>背景色 R。</summary>
        public float BackgroundR;

        /// <summary>背景色 G。</summary>
        public float BackgroundG;

        /// <summary>背景色 B。</summary>
        public float BackgroundB;

        /// <summary>背景色 A。</summary>
        public float BackgroundA;

        /// <summary>宽高比。</summary>
        public float Aspect;

        /// <summary>快照时间(UTC, ISO 8601)。</summary>
        public string CapturedUtc;

        // ---------------------------------------------------------------------
        // Unity 类型便捷访问(不参与 JSON)
        // ---------------------------------------------------------------------

        /// <summary>世界坐标。</summary>
        [CesiumJsonIgnore]
        public Vector3 Position
        {
            get { return new Vector3(PositionX, PositionY, PositionZ); }
            set { PositionX = value.x; PositionY = value.y; PositionZ = value.z; }
        }

        /// <summary>世界旋转。</summary>
        [CesiumJsonIgnore]
        public Quaternion Rotation
        {
            get { return new Quaternion(RotationX, RotationY, RotationZ, RotationW); }
            set { RotationX = value.x; RotationY = value.y; RotationZ = value.z; RotationW = value.w; }
        }

        /// <summary>世界欧拉角。</summary>
        [CesiumJsonIgnore]
        public Vector3 EulerAngles
        {
            get { return new Vector3(EulerX, EulerY, EulerZ); }
            set { EulerX = value.x; EulerY = value.y; EulerZ = value.z; }
        }

        /// <summary>背景色。</summary>
        [CesiumJsonIgnore]
        public Color BackgroundColor
        {
            get { return new Color(BackgroundR, BackgroundG, BackgroundB, BackgroundA); }
            set { BackgroundR = value.r; BackgroundG = value.g; BackgroundB = value.b; BackgroundA = value.a; }
        }

        // ---------------------------------------------------------------------
        // 采集 / 还原(需要 Unity, 只在游戏内执行)
        // ---------------------------------------------------------------------

        /// <summary>从后端采集相机状态。相机无效时返回 Valid=false 的空快照。</summary>
        public static CameraState Capture(ICameraBackend backend, object camera)
        {
            var state = new CameraState { CapturedUtc = DateTime.UtcNow.ToString("o") };
            if (backend == null || camera == null || !backend.IsAlive(camera)) return state;

            try
            {
                state.Valid = true;
                state.Name = backend.GetName(camera);
                state.Position = backend.GetPosition(camera);

                Quaternion rotation = backend.GetRotation(camera);
                state.Rotation = rotation;
                state.EulerAngles = UnityCall.ToEulerAngles(rotation);

                state.FieldOfView = backend.GetFieldOfView(camera);
                state.NearClipPlane = backend.GetNearClipPlane(camera);
                state.FarClipPlane = backend.GetFarClipPlane(camera);
                state.Orthographic = backend.GetOrthographic(camera);
                state.OrthographicSize = backend.GetOrthographicSize(camera);
                state.Enabled = backend.GetEnabled(camera);
                state.Depth = backend.GetDepth(camera);
                state.CullingMask = backend.GetCullingMask(camera);
                state.ClearFlags = backend.GetClearFlags(camera);
                state.BackgroundColor = backend.GetBackgroundColor(camera);
                state.Aspect = backend.GetAspect(camera);
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash("CAMERA", "采集相机状态", e);
            }

            return state;
        }

        /// <summary>把状态写回相机。返回是否全部成功。</summary>
        public static bool Restore(ICameraBackend backend, object camera, CameraState state)
        {
            if (backend == null || camera == null || !backend.IsAlive(camera)) return false;
            if (!state.Valid) return false;

            bool ok = true;
            try
            {
                ok &= backend.SetPosition(camera, state.Position);
                ok &= backend.SetRotation(camera, state.Rotation);
                ok &= backend.SetFieldOfView(camera, state.FieldOfView);
                ok &= backend.SetNearClipPlane(camera, state.NearClipPlane);
                ok &= backend.SetFarClipPlane(camera, state.FarClipPlane);
                ok &= backend.SetOrthographic(camera, state.Orthographic);
                ok &= backend.SetOrthographicSize(camera, state.OrthographicSize);
                ok &= backend.SetDepth(camera, state.Depth);
                ok &= backend.SetCullingMask(camera, state.CullingMask);
                ok &= backend.SetClearFlags(camera, state.ClearFlags);
                ok &= backend.SetBackgroundColor(camera, state.BackgroundColor);
                // enabled/aspect 放最后: 某些渲染管线在 disabled 状态会拒绝改参数
                ok &= backend.SetAspect(camera, state.Aspect);
                ok &= backend.SetEnabled(camera, state.Enabled);
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash("CAMERA", "恢复相机状态", e);
                return false;
            }
            return ok;
        }

        // ---------------------------------------------------------------------
        // 序列化(纯标量, 可离线测试)
        // ---------------------------------------------------------------------

        /// <summary>序列化为 JSON。</summary>
        public string ToJson()
        {
            return CesiumJson.Serialize(this);
        }

        /// <summary>序列化为便于阅读的 JSON。</summary>
        public string ToJsonPretty()
        {
            return CesiumJson.SerializePretty(this);
        }

        /// <summary>从 JSON 解析(失败返回 false, 不抛异常)。</summary>
        public static bool TryParse(string json, out CameraState state)
        {
            state = default(CameraState);
            if (string.IsNullOrEmpty(json)) return false;

            object node;
            if (!CesiumJson.TryDeserialize(json, out node)) return false;

            // 根节点必须是 JSON 对象。数组/标量映射到结构体会得到"全默认值"的假状态
            // (例如 "[1,2,3]" 会变成一个 Valid=false、什么都没读到的 CameraState), 必须判失败。
            if (!(node is System.Collections.Generic.IDictionary<string, object>)) return false;

            try
            {
                object mapped = CesiumJson.ToObject(node, typeof(CameraState));
                if (!(mapped is CameraState)) return false;
                state = (CameraState)mapped;
                return true;
            }
            catch { return false; }
        }

        /// <summary>一行摘要(日志/诊断用)。</summary>
        public string Describe()
        {
            if (!Valid) return "CameraState(无效)";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} pos=({1:F2},{2:F2},{3:F2}) euler=({4:F1},{5:F1},{6:F1}) fov={7:F1} near={8:F3} far={9:F1} ortho={10} enabled={11} depth={12}",
                Name, PositionX, PositionY, PositionZ,
                EulerX, EulerY, EulerZ,
                FieldOfView, NearClipPlane, FarClipPlane, Orthographic, Enabled, Depth);
        }

        /// <summary>摘要文本。</summary>
        public override string ToString()
        {
            return Describe();
        }
    }
}
