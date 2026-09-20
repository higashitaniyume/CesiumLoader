using System;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 自由相机的纯数学部分(不依赖 UnityEngine, 因此可以脱离游戏做单元测试)。
    ///
    /// 为什么单独抽出来: 相机数学(mod 最容易写错的地方)如果直接写在 mod 里用
    /// Quaternion/Vector3, 就只能进游戏才能验证。这里用标量 + System.Math 实现,
    /// 单元测试可以精确断言"yaw=90° 时前进方向是 +X"这类结论。
    ///
    /// 坐标系约定(与 Unity 左手系一致):
    ///  - yaw 绕 Y 轴, 0° 朝 +Z, +90° 朝 +X;
    ///  - pitch 绕 X 轴, <b>正值为低头</b>(与 Quaternion.Euler 一致);
    ///  - up 为世界 +Y。
    /// </summary>
    public static class FreeCameraMath
    {
        /// <summary>俯仰角下限(度)。</summary>
        public const float MinPitch = -89f;

        /// <summary>俯仰角上限(度)。</summary>
        public const float MaxPitch = 89f;

        /// <summary>默认 FOV 下限。</summary>
        public const float MinFieldOfView = 10f;

        /// <summary>默认 FOV 上限。</summary>
        public const float MaxFieldOfView = 120f;

        /// <summary>轨道模式: 相机到观察点的最近距离。</summary>
        public const float MinOrbitDistance = 2f;

        /// <summary>轨道模式: 相机到观察点的最远距离。</summary>
        public const float MaxOrbitDistance = 120f;

        /// <summary>加速倍率。</summary>
        public const float FastMultiplier = 4f;

        /// <summary>减速倍率。</summary>
        public const float SlowMultiplier = 0.25f;

        private const double Deg2Rad = Math.PI / 180.0;

        /// <summary>把角度规整到 (-180, 180]。</summary>
        public static float NormalizeAngle180(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees)) return 0f;
            float value = degrees % 360f;
            if (value > 180f) value -= 360f;
            if (value <= -180f) value += 360f;
            return value;
        }

        /// <summary>把俯仰角夹到安全范围(避免视线翻转)。</summary>
        public static float ClampPitch(float pitch)
        {
            if (float.IsNaN(pitch)) return 0f;
            if (pitch < MinPitch) return MinPitch;
            if (pitch > MaxPitch) return MaxPitch;
            return pitch;
        }

        /// <summary>
        /// 由 yaw/pitch 求朝向单位向量(= Unity 的 <c>transform.forward</c>)。
        /// </summary>
        /// <param name="yawDeg">yaw(度)。</param>
        /// <param name="pitchDeg">pitch(度, 正值为低头)。</param>
        /// <param name="x">结果 X。</param>
        /// <param name="y">结果 Y。</param>
        /// <param name="z">结果 Z。</param>
        public static void ForwardVector(float yawDeg, float pitchDeg, out float x, out float y, out float z)
        {
            double yaw = NormalizeAngle180(yawDeg) * Deg2Rad;
            double pitch = ClampPitch(pitchDeg) * Deg2Rad;
            double cosPitch = Math.Cos(pitch);
            x = (float)(Math.Sin(yaw) * cosPitch);
            y = (float)(-Math.Sin(pitch));
            z = (float)(Math.Cos(yaw) * cosPitch);
        }

        /// <summary>
        /// 应用鼠标转向。yaw 会累加并规整; pitch 会夹紧。
        /// </summary>
        /// <param name="yaw">当前 yaw(度)。</param>
        /// <param name="pitch">当前 pitch(度)。</param>
        /// <param name="mouseX">鼠标水平增量。</param>
        /// <param name="mouseY">鼠标垂直增量。</param>
        /// <param name="sensitivity">灵敏度。</param>
        /// <param name="invertY">是否反转垂直。</param>
        /// <param name="newYaw">结果 yaw。</param>
        /// <param name="newPitch">结果 pitch。</param>
        public static void ApplyMouseLook(float yaw, float pitch, float mouseX, float mouseY,
            float sensitivity, bool invertY, out float newYaw, out float newPitch)
        {
            newYaw = NormalizeAngle180(yaw + mouseX * sensitivity);
            newPitch = ClampPitch(pitch + (invertY ? mouseY : -mouseY) * sensitivity);
        }

        /// <summary>
        /// 轨道模式的鼠标转向。语义与 <see cref="ApplyMouseLook"/> 相反:
        /// <b>鼠标上移 = 抬高相机</b>(相机升到观察点上方并俯视), 这才是轨道相机的常规手势
        /// (像把球拖到头顶看下去)。<paramref name="invertY"/>=true 时反转。
        /// </summary>
        /// <param name="yaw">当前 yaw(度)。</param>
        /// <param name="elevation">当前仰角(度, 正值 = 相机在观察点上方俯视)。</param>
        /// <param name="mouseX">鼠标水平增量。</param>
        /// <param name="mouseY">鼠标垂直增量。</param>
        /// <param name="sensitivity">灵敏度。</param>
        /// <param name="invertY">是否反转垂直。</param>
        /// <param name="newYaw">结果 yaw。</param>
        /// <param name="newElevation">结果仰角。</param>
        public static void ApplyOrbitLook(float yaw, float elevation, float mouseX, float mouseY,
            float sensitivity, bool invertY, out float newYaw, out float newElevation)
        {
            newYaw = NormalizeAngle180(yaw + mouseX * sensitivity);
            newElevation = ClampPitch(elevation + (invertY ? -mouseY : mouseY) * sensitivity);
        }

        /// <summary>
        /// 轨道模式的核心: 把相机放到以观察点为球心的球面上, 朝向始终指向观察点。
        ///
        /// 几何: 相机位于 <c>观察点 - forward * distance</c>, 因此
        /// 仰角 &gt; 0 时相机在观察点<b>上方</b>并向下看 —— 这正是"抬高相机看全局"。
        /// 配合 <c>Quaternion.Euler(elevation, yaw, 0)</c> 的朝向即可直视观察点。
        /// </summary>
        /// <param name="pivotX">观察点 X。</param>
        /// <param name="pivotY">观察点 Y。</param>
        /// <param name="pivotZ">观察点 Z。</param>
        /// <param name="yawDeg">绕观察点的水平角(度)。</param>
        /// <param name="elevationDeg">仰角(度), 正值 = 相机在上方俯视。</param>
        /// <param name="distance">相机到观察点的距离。</param>
        /// <param name="x">结果相机 X。</param>
        /// <param name="y">结果相机 Y。</param>
        /// <param name="z">结果相机 Z。</param>
        public static void OrbitPosition(float pivotX, float pivotY, float pivotZ,
            float yawDeg, float elevationDeg, float distance,
            out float x, out float y, out float z)
        {
            x = pivotX; y = pivotY; z = pivotZ;
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance <= 0f) return;

            double yaw = NormalizeAngle180(yawDeg) * Deg2Rad;
            double elevation = ClampPitch(elevationDeg) * Deg2Rad;
            double cosE = Math.Cos(elevation);
            double sinE = Math.Sin(elevation);

            x = (float)(pivotX - Math.Sin(yaw) * cosE * distance);
            y = (float)(pivotY + sinE * distance);           // 仰角>0 -> 相机高于观察点
            z = (float)(pivotZ - Math.Cos(yaw) * cosE * distance);
        }

        /// <summary>计算速度倍率(Shift 加速 / Ctrl 减速)。</summary>
        public static float SpeedMultiplier(bool fast, bool slow,
            float fastMultiplier = FastMultiplier, float slowMultiplier = SlowMultiplier)
        {
            float multiplier = 1f;
            if (fast) multiplier *= fastMultiplier;
            if (slow) multiplier *= slowMultiplier;
            return multiplier;
        }

        /// <summary>
        /// 计算本帧的世界位移。
        /// </summary>
        /// <param name="yawDeg">yaw(度)。</param>
        /// <param name="pitchDeg">pitch(度, 正值为低头)。</param>
        /// <param name="forward">前后输入(-1..1), 正值向前。</param>
        /// <param name="right">左右输入(-1..1), 正值向右。</param>
        /// <param name="up">升降输入(-1..1), 正值向上。</param>
        /// <param name="speed">速度(单位/秒)。</param>
        /// <param name="deltaTime">帧时长(秒)。</param>
        /// <param name="dx">结果位移 X。</param>
        /// <param name="dy">结果位移 Y。</param>
        /// <param name="dz">结果位移 Z。</param>
        public static void ComposeDelta(float yawDeg, float pitchDeg, float forward, float right, float up,
            float speed, float deltaTime, out float dx, out float dy, out float dz)
        {
            dx = 0f; dy = 0f; dz = 0f;
            if (speed == 0f || deltaTime == 0f) return;
            if (forward == 0f && right == 0f && up == 0f) return;

            double yaw = NormalizeAngle180(yawDeg) * Deg2Rad;

            double cosYaw = Math.Cos(yaw);
            double sinYaw = Math.Sin(yaw);

            // forward 与 ForwardVector 共用同一套公式(单一来源, 避免两处漂移)
            float fx32, fy32, fz32;
            ForwardVector(yawDeg, pitchDeg, out fx32, out fy32, out fz32);
            double fx = fx32;
            double fy = fy32;
            double fz = fz32;

            // right: 水平面内垂直于 forward
            double rx = cosYaw;
            double ry = 0.0;
            double rz = -sinYaw;

            double vx = fx * forward + rx * right;
            double vy = fy * forward + ry * right + up;
            double vz = fz * forward + rz * right;

            double length = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            if (length < 1e-9) return; // 输入互相抵消

            // 归一化后乘速度(斜向移动不会变快)
            double scale = speed * deltaTime / length;
            dx = (float)(vx * scale);
            dy = (float)(vy * scale);
            dz = (float)(vz * scale);
        }

        /// <summary>按滚轮增量调整 FOV 并夹紧。</summary>
        public static float ApplyScrollToFieldOfView(float fieldOfView, float scroll,
            float degreesPerNotch = 5f, float min = MinFieldOfView, float max = MaxFieldOfView)
        {
            if (scroll == 0f || float.IsNaN(scroll)) return Clamp(fieldOfView, min, max);
            return Clamp(fieldOfView - scroll * degreesPerNotch, min, max);
        }

        /// <summary>
        /// 按滚轮增量推拉相机与观察点的距离并夹紧(滚轮上 = 拉近, 与 FOV 方向一致)。
        /// 轨道模式用来"把相机往后拉远看全局"。
        /// </summary>
        public static float ApplyScrollToDistance(float distance, float scroll,
            float unitsPerNotch = 1.5f, float min = MinOrbitDistance, float max = MaxOrbitDistance)
        {
            if (scroll == 0f || float.IsNaN(scroll)) return Clamp(distance, min, max);
            return Clamp(distance - scroll * unitsPerNotch, min, max);
        }

        /// <summary>限制数值范围(NaN 归到 min)。</summary>
        public static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>两位小数的文本(日志用)。</summary>
        public static string Format(float value)
        {
            return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
