using System;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 自由相机数学(纯标量实现, 不依赖 UnityEngine) —— 这是 FreeCamera 最易出错的部分。
    /// 坐标系约定: yaw 绕 Y, 0° 朝 +Z, +90° 朝 +X; pitch 正值为低头; up 为世界 +Y。
    /// </summary>
    public class FreeCameraMathTests
    {
        private const float Eps = 1e-4f;

        // ---------------- 角度规整 ----------------

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(90f, 90f)]
        [InlineData(180f, 180f)]
        [InlineData(190f, -170f)]
        [InlineData(-190f, 170f)]
        [InlineData(360f, 0f)]
        [InlineData(540f, 180f)]
        [InlineData(-540f, 180f)]
        public void NormalizeAngle180_WrapsIntoRange(float input, float expected)
        {
            Assert.Equal(expected, FreeCameraMath.NormalizeAngle180(input), 3);
        }

        [Fact]
        public void NormalizeAngle180_TreatsNaNAsZero()
        {
            Assert.Equal(0f, FreeCameraMath.NormalizeAngle180(float.NaN));
            Assert.Equal(0f, FreeCameraMath.NormalizeAngle180(float.PositiveInfinity));
        }

        // ---------------- 俯仰夹紧 ----------------

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(-100f, -89f)]
        [InlineData(100f, 89f)]
        [InlineData(45f, 45f)]
        public void ClampPitch_PreventsFlippingOver(float input, float expected)
        {
            Assert.Equal(expected, FreeCameraMath.ClampPitch(input), 3);
        }

        [Fact]
        public void ClampPitch_NaNBecomesZero()
        {
            Assert.Equal(0f, FreeCameraMath.ClampPitch(float.NaN));
        }

        // ---------------- 鼠标转向 ----------------

        [Fact]
        public void ApplyMouseLook_AccumulatesYawAndInvertsPitchByDefault()
        {
            float yaw, pitch;

            FreeCameraMath.ApplyMouseLook(10f, 0f, 5f, 5f, 1f, false, out yaw, out pitch);

            Assert.Equal(15f, yaw, 3);
            Assert.Equal(-5f, pitch, 3);   // 默认: 鼠标上推 → 抬头(负 pitch)
        }

        [Fact]
        public void ApplyMouseLook_InvertYFlipsVerticalDirection()
        {
            float yaw, pitch;

            FreeCameraMath.ApplyMouseLook(0f, 0f, 0f, 5f, 1f, true, out yaw, out pitch);

            Assert.Equal(0f, yaw, 3);
            Assert.Equal(5f, pitch, 3);
        }

        [Fact]
        public void ApplyMouseLook_ClampsPitchAndWrapsYaw()
        {
            float yaw, pitch;

            FreeCameraMath.ApplyMouseLook(179f, 0f, 10f, 1000f, 1f, false, out yaw, out pitch);

            Assert.Equal(-171f, yaw, 3);
            Assert.Equal(-89f, pitch, 3);
        }

        [Fact]
        public void ApplyMouseLook_ZeroSensitivity_KeepsAngles()
        {
            float yaw, pitch;

            FreeCameraMath.ApplyMouseLook(30f, 20f, 100f, 100f, 0f, false, out yaw, out pitch);

            Assert.Equal(30f, yaw, 3);
            Assert.Equal(20f, pitch, 3);
        }

        // ---------------- 速度倍率 ----------------

        [Theory]
        [InlineData(false, false, 1f)]
        [InlineData(true, false, 4f)]
        [InlineData(false, true, 0.25f)]
        [InlineData(true, true, 1f)]
        public void SpeedMultiplier_ShiftAndCtrl(bool fast, bool slow, float expected)
        {
            Assert.Equal(expected, FreeCameraMath.SpeedMultiplier(fast, slow), 3);
        }

        // ---------------- 位移合成 ----------------

        [Fact]
        public void ComposeDelta_ForwardAtZeroYaw_MovesPositiveZ()
        {
            float dx, dy, dz;

            FreeCameraMath.ComposeDelta(0f, 0f, 1f, 0f, 0f, 10f, 1f, out dx, out dy, out dz);

            Assert.Equal(0f, dx, 3);
            Assert.Equal(0f, dy, 3);
            Assert.Equal(10f, dz, 3);
        }

        [Fact]
        public void ComposeDelta_ForwardAt90Yaw_MovesPositiveX()
        {
            float dx, dy, dz;

            FreeCameraMath.ComposeDelta(90f, 0f, 1f, 0f, 0f, 10f, 1f, out dx, out dy, out dz);

            Assert.Equal(10f, dx, 3);
            Assert.Equal(0f, dy, 3);
            Assert.Equal(0f, dz, 3);
        }

        [Fact]
        public void ComposeDelta_BackwardsIsOppositeOfForward()
        {
            float fx, fy, fz, bx, by, bz;

            FreeCameraMath.ComposeDelta(30f, 10f, 1f, 0f, 0f, 5f, 1f, out fx, out fy, out fz);
            FreeCameraMath.ComposeDelta(30f, 10f, -1f, 0f, 0f, 5f, 1f, out bx, out by, out bz);

            Assert.Equal(-fx, bx, 4);
            Assert.Equal(-fy, by, 4);
            Assert.Equal(-fz, bz, 4);
        }

        [Fact]
        public void ComposeDelta_StrafeRightAtZeroYaw_MovesPositiveX()
        {
            float dx, dy, dz;

            FreeCameraMath.ComposeDelta(0f, 0f, 0f, 1f, 0f, 4f, 1f, out dx, out dy, out dz);

            Assert.Equal(4f, dx, 3);
            Assert.Equal(0f, dy, 3);
            Assert.Equal(0f, dz, 3);
        }

        [Fact]
        public void ComposeDelta_VerticalInput_UsesWorldUp()
        {
            float dx, dy, dz;

            FreeCameraMath.ComposeDelta(123f, 45f, 0f, 0f, 1f, 8f, 1f, out dx, out dy, out dz);

            Assert.Equal(0f, dx, 3);
            Assert.Equal(8f, dy, 3);
            Assert.Equal(0f, dz, 3);
        }

        [Fact]
        public void ComposeDelta_LookingDownMovesDownwards()
        {
            float dx, dy, dz;

            FreeCameraMath.ComposeDelta(0f, 45f, 1f, 0f, 0f, 10f, 1f, out dx, out dy, out dz);

            Assert.True(dy < 0f, "pitch 正值为低头, 前进应向下");
            Assert.True(dz > 0f);
            Assert.Equal(0f, dx, 3);
        }

        [Fact]
        public void ComposeDelta_DiagonalMovement_IsNormalized()
        {
            float dx, dy, dz;

            FreeCameraMath.ComposeDelta(0f, 0f, 1f, 1f, 0f, 10f, 1f, out dx, out dy, out dz);

            float length = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            Assert.Equal(10f, length, 3);   // 斜向不能比直行快
        }

        [Fact]
        public void ComposeDelta_ZeroSpeedTimeOrNoInput_ProducesNoMovement()
        {
            float dx, dy, dz;

            FreeCameraMath.ComposeDelta(0f, 0f, 1f, 0f, 0f, 0f, 1f, out dx, out dy, out dz);
            Assert.Equal(0f, dx); Assert.Equal(0f, dy); Assert.Equal(0f, dz);

            FreeCameraMath.ComposeDelta(0f, 0f, 1f, 0f, 0f, 10f, 0f, out dx, out dy, out dz);
            Assert.Equal(0f, dx); Assert.Equal(0f, dy); Assert.Equal(0f, dz);

            FreeCameraMath.ComposeDelta(0f, 0f, 0f, 0f, 0f, 10f, 1f, out dx, out dy, out dz);
            Assert.Equal(0f, dx); Assert.Equal(0f, dy); Assert.Equal(0f, dz);
        }

        [Fact]
        public void ComposeDelta_OpposingInputsCancelOut()
        {
            float dx, dy, dz;

            // 前进 + 后退同时按下 → 抵消, 不应产生位移
            FreeCameraMath.ComposeDelta(0f, 0f, 1f, 0f, 0f, 10f, 1f, out dx, out dy, out dz);
            float fx = dx, fy = dy, fz = dz;
            FreeCameraMath.ComposeDelta(0f, 0f, -1f, 0f, 0f, 10f, 1f, out dx, out dy, out dz);

            Assert.Equal(fx, -dx, 3);
            Assert.Equal(fy, -dy, 3);
            Assert.Equal(fz, -dz, 3);
        }

        [Fact]
        public void ComposeDelta_ScalesLinearlyWithDeltaTime()
        {
            float ax, ay, az, bx, by, bz;

            FreeCameraMath.ComposeDelta(20f, 5f, 1f, 0f, 0f, 6f, 0.5f, out ax, out ay, out az);
            FreeCameraMath.ComposeDelta(20f, 5f, 1f, 0f, 0f, 6f, 1.0f, out bx, out by, out bz);

            Assert.Equal(ax * 2f, bx, 3);
            Assert.Equal(ay * 2f, by, 3);
            Assert.Equal(az * 2f, bz, 3);
        }

        // ---------------- FOV ----------------

        [Fact]
        public void ApplyScrollToFieldOfView_ScrollsAndClamps()
        {
            Assert.Equal(55f, FreeCameraMath.ApplyScrollToFieldOfView(60f, 1f), 3);
            Assert.Equal(65f, FreeCameraMath.ApplyScrollToFieldOfView(60f, -1f), 3);
            Assert.Equal(FreeCameraMath.MinFieldOfView, FreeCameraMath.ApplyScrollToFieldOfView(11f, 10f), 3);
            Assert.Equal(FreeCameraMath.MaxFieldOfView, FreeCameraMath.ApplyScrollToFieldOfView(119f, -10f), 3);
        }

        [Fact]
        public void ApplyScrollToFieldOfView_NoScrollOnlyClamps()
        {
            Assert.Equal(60f, FreeCameraMath.ApplyScrollToFieldOfView(60f, 0f), 3);
            Assert.Equal(FreeCameraMath.MaxFieldOfView, FreeCameraMath.ApplyScrollToFieldOfView(500f, 0f), 3);
            Assert.Equal(30f, FreeCameraMath.ApplyScrollToFieldOfView(30f, float.NaN), 3);
        }

        // ---------------- 朝向向量 ----------------

        [Fact]
        public void ForwardVector_MatchesDocumentedConvention()
        {
            float x, y, z;

            FreeCameraMath.ForwardVector(0f, 0f, out x, out y, out z);
            Assert.Equal(0f, x, 3); Assert.Equal(0f, y, 3); Assert.Equal(1f, z, 3);   // yaw=0 -> +Z

            FreeCameraMath.ForwardVector(90f, 0f, out x, out y, out z);
            Assert.Equal(1f, x, 3); Assert.Equal(0f, y, 3); Assert.Equal(0f, z, 3);   // yaw=90 -> +X

            // pitch 会被 ClampPitch 夹在 ±89°(防视线翻转), 所以只能到"几乎正下方/正上方"
            FreeCameraMath.ForwardVector(0f, 90f, out x, out y, out z);
            Assert.True(y < -0.99f, "pitch>0 应向下看");
            Assert.True(Math.Abs(x) < 0.001f);

            FreeCameraMath.ForwardVector(0f, -90f, out x, out y, out z);
            Assert.True(y > 0.99f, "pitch<0 应向上看");
        }

        [Fact]
        public void ForwardVector_IsAlwaysUnitLength()
        {
            for (float yaw = -180f; yaw <= 180f; yaw += 45f)
            {
                for (float pitch = -89f; pitch <= 89f; pitch += 30f)
                {
                    float x, y, z;
                    FreeCameraMath.ForwardVector(yaw, pitch, out x, out y, out z);
                    float len = (float)Math.Sqrt(x * x + y * y + z * z);
                    Assert.Equal(1f, len, 3);
                }
            }
        }

        // ---------------- 轨道(环绕观察)模式 ----------------

        [Fact]
        public void OrbitPosition_KeepsCameraAtGivenDistanceBehindPivot()
        {
            float x, y, z;

            // 仰角 0: 相机与观察点同一水平面, 且位于观察点后方 distance 处
            FreeCameraMath.OrbitPosition(0f, 0f, 0f, 0f, 0f, 10f, out x, out y, out z);
            Assert.Equal(0f, x, 3);
            Assert.Equal(0f, y, 3);
            Assert.Equal(-10f, z, 3);   // yaw=0 时看向 +Z, 所以相机在 -Z

            // yaw=90: 相机绕到 -X
            FreeCameraMath.OrbitPosition(0f, 0f, 0f, 90f, 0f, 10f, out x, out y, out z);
            Assert.Equal(-10f, x, 3);
            Assert.Equal(0f, y, 3);
            Assert.Equal(0f, z, 3);
        }

        [Fact]
        public void OrbitPosition_PositiveElevation_PutsCameraAbovePivot()
        {
            float x, y, z;

            // 这正是"把相机抬高俯视"的数学表达: 观察点在 (0,5,0), 仰角 45°, 距离 10
            FreeCameraMath.OrbitPosition(0f, 5f, 0f, 0f, 45f, 10f, out x, out y, out z);

            Assert.Equal(0f, x, 3);
            Assert.Equal(5f + 7.0710678f, y, 3);    // sin45 * 10 = 7.0711
            Assert.True(y > 5f, "仰角 > 0 时相机必须高于观察点");
            Assert.Equal(-7.0710678f, z, 3);        // cos45 * 10
        }

        [Fact]
        public void OrbitPosition_ElevationShowsAllowedRange()
        {
            float x, y, z;

            // 仰角被夹到 +89: 相机几乎在观察点正上方
            FreeCameraMath.OrbitPosition(0f, 10f, 0f, 0f, 1000f, 20f, out x, out y, out z);
            Assert.True(y > 10f);
            Assert.True(Math.Abs(z) < 0.5f, "俯视时相机应几乎在观察点正上方");

            // 仰角 -89: 相机几乎在观察点正下方(抬头看)
            FreeCameraMath.OrbitPosition(0f, 10f, 0f, 0f, -1000f, 20f, out x, out y, out z);
            Assert.True(y < 10f);
        }

        [Fact]
        public void OrbitPosition_AlwaysKeepsRequestedDistanceToPivot()
        {
            // 环绕的硬性不变量: 无论怎么转, 相机到观察点的距离不变
            float pivotX = 3f, pivotY = -2f, pivotZ = 7f;

            for (float yaw = -180f; yaw <= 180f; yaw += 45f)
            {
                for (float elev = -80f; elev <= 80f; elev += 20f)
                {
                    foreach (float d in new[] { 2f, 12f, 120f })
                    {
                        float x, y, z;
                        FreeCameraMath.OrbitPosition(pivotX, pivotY, pivotZ, yaw, elev, d, out x, out y, out z);

                        float dx = x - pivotX, dy = y - pivotY, dz = z - pivotZ;
                        float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        Assert.Equal(d, len, 2);
                    }
                }
            }
        }

        [Fact]
        public void OrbitPosition_IsInverseOfForwardVector_SoEnteringDoesNotJumpTheView()
        {
            // 这是 FreeCameraController.SetupOrbit 依赖的性质:
            //   观察点 = 相机位置 + forward × distance
            // 于是 OrbitPosition 反推出的相机位置必须等于原相机位置 —— 按 F1 进入时画面不跳。
            float[] yaws = { 0f, 37f, 90f, 180f, -120f };
            float[] elevations = { -30f, 0f, 15f, 45f, 80f };

            for (int i = 0; i < yaws.Length; i++)
            {
                float yaw = yaws[i], elevation = elevations[i];
                float camX = 11f, camY = 22f, camZ = -33f, d = 12f;

                float fx, fy, fz;
                FreeCameraMath.ForwardVector(yaw, elevation, out fx, out fy, out fz);

                float pivotX = camX + fx * d;
                float pivotY = camY + fy * d;
                float pivotZ = camZ + fz * d;

                float x, y, z;
                FreeCameraMath.OrbitPosition(pivotX, pivotY, pivotZ, yaw, elevation, d, out x, out y, out z);

                Assert.Equal(camX, x, 3);
                Assert.Equal(camY, y, 3);
                Assert.Equal(camZ, z, 3);
            }
        }

        [Fact]
        public void OrbitPosition_InvalidDistance_FallsBackToPivot()
        {
            float x, y, z;

            FreeCameraMath.OrbitPosition(1f, 2f, 3f, 45f, 45f, 0f, out x, out y, out z);
            Assert.Equal(1f, x); Assert.Equal(2f, y); Assert.Equal(3f, z);

            FreeCameraMath.OrbitPosition(1f, 2f, 3f, 45f, 45f, -5f, out x, out y, out z);
            Assert.Equal(1f, x); Assert.Equal(2f, y); Assert.Equal(3f, z);

            FreeCameraMath.OrbitPosition(1f, 2f, 3f, 45f, 45f, float.NaN, out x, out y, out z);
            Assert.Equal(1f, x); Assert.Equal(2f, y); Assert.Equal(3f, z);
        }

        [Fact]
        public void ApplyOrbitLook_MouseUpRaisesTheCamera_OppositeOfFlyMode()
        {
            float yaw, elevation;

            // 轨道模式: 鼠标上移 -> 仰角增大 -> 相机升高俯视
            FreeCameraMath.ApplyOrbitLook(0f, 10f, 0f, 5f, 1f, false, out yaw, out elevation);
            Assert.Equal(15f, elevation, 3);

            // 同一输入在飞行模式下是"抬头", 两者必须相反(否则轨道模式会感觉上下颠倒)
            float flyYaw, flyPitch;
            FreeCameraMath.ApplyMouseLook(0f, 10f, 0f, 5f, 1f, false, out flyYaw, out flyPitch);
            Assert.Equal(5f, flyPitch, 3);
            Assert.True(elevation > flyPitch);
        }

        [Fact]
        public void ApplyOrbitLook_InvertYFlipsVerticalDirection()
        {
            float yaw, elevation;

            FreeCameraMath.ApplyOrbitLook(0f, 10f, 0f, 5f, 1f, true, out yaw, out elevation);

            Assert.Equal(0f, yaw, 3);
            Assert.Equal(5f, elevation, 3);
        }

        [Fact]
        public void ApplyOrbitLook_ClampsElevationAndWrapsYaw()
        {
            float yaw, elevation;

            FreeCameraMath.ApplyOrbitLook(179f, 0f, 10f, 1000f, 1f, false, out yaw, out elevation);
            Assert.Equal(-171f, yaw, 3);
            Assert.Equal(FreeCameraMath.MaxPitch, elevation, 3);

            FreeCameraMath.ApplyOrbitLook(0f, 0f, 0f, -1000f, 1f, false, out yaw, out elevation);
            Assert.Equal(FreeCameraMath.MinPitch, elevation, 3);
        }

        // ---------------- 推拉距离 ----------------

        [Fact]
        public void ApplyScrollToDistance_ScrollsAndClamps()
        {
            // 滚轮上 = 拉近(与 FOV 的方向一致)
            Assert.Equal(10.5f, FreeCameraMath.ApplyScrollToDistance(12f, 1f), 3);
            Assert.Equal(13.5f, FreeCameraMath.ApplyScrollToDistance(12f, -1f), 3);

            Assert.Equal(FreeCameraMath.MinOrbitDistance,
                FreeCameraMath.ApplyScrollToDistance(3f, 10f), 3);
            Assert.Equal(FreeCameraMath.MaxOrbitDistance,
                FreeCameraMath.ApplyScrollToDistance(118f, -10f), 3);
        }

        [Fact]
        public void ApplyScrollToDistance_NoScrollOnlyClamps()
        {
            Assert.Equal(12f, FreeCameraMath.ApplyScrollToDistance(12f, 0f), 3);
            Assert.Equal(FreeCameraMath.MinOrbitDistance, FreeCameraMath.ApplyScrollToDistance(0.5f, 0f), 3);
            Assert.Equal(FreeCameraMath.MaxOrbitDistance, FreeCameraMath.ApplyScrollToDistance(999f, 0f), 3);
            Assert.Equal(12f, FreeCameraMath.ApplyScrollToDistance(12f, float.NaN), 3);
        }

        [Fact]
        public void ApplyScrollToDistance_CustomStep()
        {
            Assert.Equal(8f, FreeCameraMath.ApplyScrollToDistance(10f, 1f, 2f), 3);
        }

        [Fact]
        public void OrbitConstants_MatchDocumentedDefaults()
        {
            Assert.Equal(2f, FreeCameraMath.MinOrbitDistance);
            Assert.Equal(120f, FreeCameraMath.MaxOrbitDistance);
        }

        // ---------------- 杂项 ----------------

        [Fact]
        public void Clamp_NaNAndBounds()
        {
            Assert.Equal(5f, FreeCameraMath.Clamp(5f, 0f, 10f));
            Assert.Equal(0f, FreeCameraMath.Clamp(-1f, 0f, 10f));
            Assert.Equal(10f, FreeCameraMath.Clamp(11f, 0f, 10f));
            Assert.Equal(0f, FreeCameraMath.Clamp(float.NaN, 0f, 10f));
        }

        [Fact]
        public void Format_UsesInvariantCulture()
        {
            Assert.Equal("1.5", FreeCameraMath.Format(1.5f));
            Assert.Equal("2", FreeCameraMath.Format(2f));
        }

        // ---------------- 游戏内调高度(Ctrl + "="/"-") ----------------

        [Fact]
        public void ApplyHeightStep_MovesByOneStepInTheGivenDirection()
        {
            Assert.Equal(170f, FreeCameraMath.ApplyHeightStep(160f, 1f, 10f));
            Assert.Equal(150f, FreeCameraMath.ApplyHeightStep(160f, -1f, 10f));
            // 步进用配置里的值(例如 25m 一档)
            Assert.Equal(185f, FreeCameraMath.ApplyHeightStep(160f, 1f, 25f));
        }

        [Theory]
        [InlineData(FreeCameraMath.MinCameraHeight)]     // 5: 已经在下限
        [InlineData(0f)]
        [InlineData(-100f)]
        [InlineData(float.NaN)]
        public void ApplyHeightStep_ClampsToMinimum(float start)
        {
            float result = FreeCameraMath.ApplyHeightStep(start, -1f, 10f);
            Assert.Equal(FreeCameraMath.MinCameraHeight, result);
        }

        [Theory]
        [InlineData(FreeCameraMath.MaxCameraHeight)]     // 2000: 已经在上限
        [InlineData(5000f)]
        public void ApplyHeightStep_ClampsToMaximum(float start)
        {
            float result = FreeCameraMath.ApplyHeightStep(start, 1f, 10f);
            Assert.Equal(FreeCameraMath.MaxCameraHeight, result);
        }

        [Fact]
        public void ApplyHeightStep_AlreadyAtLimitReturnsSameValue()
        {
            // 顶到极限时返回值不变 -> 调用方据此提示"已到极限"(别把 NaN/越界当成一次有效调整)
            Assert.Equal(FreeCameraMath.MaxCameraHeight,
                FreeCameraMath.ApplyHeightStep(FreeCameraMath.MaxCameraHeight, 1f, 10f));
            Assert.Equal(FreeCameraMath.MinCameraHeight,
                FreeCameraMath.ApplyHeightStep(FreeCameraMath.MinCameraHeight, -1f, 10f));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-5f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void ApplyHeightStep_FallsBackToDefaultStepWhenStepIsUnusable(float step)
        {
            // 配置里把 heightStep 写成 0/负数/NaN 时不能让"按键没反应", 退回默认步进
            Assert.Equal(160f + FreeCameraMath.DefaultHeightStep,
                FreeCameraMath.ApplyHeightStep(160f, 1f, step));
        }

        [Fact]
        public void ApplyHeightStep_IgnoresUsableButZeroDelta()
        {
            // delta 为 0/NaN/Inf 时只是把当前高度夹紧, 不移动
            Assert.Equal(160f, FreeCameraMath.ApplyHeightStep(160f, 0f, 10f));
            Assert.Equal(160f, FreeCameraMath.ApplyHeightStep(160f, float.NaN, 10f));
            Assert.Equal(FreeCameraMath.MaxCameraHeight,
                FreeCameraMath.ApplyHeightStep(9999f, float.PositiveInfinity, 10f));
        }

        [Fact]
        public void Constants_MatchDocumentedDefaults()
        {
            Assert.Equal(-89f, FreeCameraMath.MinPitch);
            Assert.Equal(89f, FreeCameraMath.MaxPitch);
            Assert.Equal(10f, FreeCameraMath.MinFieldOfView);
            Assert.Equal(120f, FreeCameraMath.MaxFieldOfView);
            Assert.Equal(4f, FreeCameraMath.FastMultiplier);
            Assert.Equal(0.25f, FreeCameraMath.SlowMultiplier);
            Assert.Equal(5f, FreeCameraMath.MinCameraHeight);
            Assert.Equal(2000f, FreeCameraMath.MaxCameraHeight);
            Assert.Equal(10f, FreeCameraMath.DefaultHeightStep);
            Assert.True(Eps > 0f);
        }
    }
}
