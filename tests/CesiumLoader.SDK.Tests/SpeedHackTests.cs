using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 变速 (SpeedHack)。分两块:
    ///  - 热键用的纯计算(StepSpeed / ClampSpeed): 完全离线, 覆盖边界与脏配置;
    ///  - 没有加载器时的行为: 必须"安全降级"(返回默认值/false), 绝不抛异常 —— 测试宿主里
    ///    没有加载器的 ap_speed_* 导出, 正好是这种环境。
    /// </summary>
    public class SpeedHackTests
    {
        private const double Eps = 1e-9;

        // ---------------- 纯计算 ----------------

        [Fact]
        public void StepSpeed_MovesByStepInTheGivenDirection()
        {
            Assert.Equal(2.5, SpeedHack.StepSpeed(2.0, 1.0), Eps);
            Assert.Equal(1.5, SpeedHack.StepSpeed(2.0, -1.0), Eps);
            // 步进用配置里的值
            Assert.Equal(2.25, SpeedHack.StepSpeed(2.0, 1.0, 0.25), Eps);
        }

        [Fact]
        public void StepSpeed_ClampsToConfiguredBounds()
        {
            Assert.Equal(4.0, SpeedHack.StepSpeed(3.8, 1.0, 0.5, 0.5, 4.0), Eps);   // 上界
            Assert.Equal(0.5, SpeedHack.StepSpeed(0.6, -1.0, 0.5, 0.5, 4.0), Eps); // 下界
        }

        [Fact]
        public void StepSpeed_AtLimitReturnsSameValue()
        {
            // 顶到极限时返回值不变 -> 调用方据此提示"已是极限", 不必写盘
            Assert.Equal(4.0, SpeedHack.StepSpeed(4.0, 1.0, 0.5, 0.5, 4.0), Eps);
            Assert.Equal(0.5, SpeedHack.StepSpeed(0.5, -1.0, 0.5, 0.5, 4.0), Eps);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void StepSpeed_FallsBackToDefaultStepWhenStepIsUnusable(double step)
        {
            // heightStep/speedStep 写成 0/负数/NaN 也不能变成"按键没反应"
            Assert.Equal(2.0 + SpeedHack.DefaultSpeedStep, SpeedHack.StepSpeed(2.0, 1.0, step), Eps);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(0.0)]
        [InlineData(-2.0)]
        public void StepSpeed_FallsBackToSdkMinWhenMinIsUnusable(double min)
        {
            // min 写成 0/负数/NaN -> 用 SDK 默认下限(绝不能把倍率夹成 0 —— 那等于游戏卡死)
            Assert.Equal(SpeedHack.MinSpeed, SpeedHack.StepSpeed(1.0, -1.0, 100.0, min, 8.0), Eps);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(0.0)]
        [InlineData(-2.0)]
        public void StepSpeed_FallsBackToSdkMaxWhenMaxIsUnusable(double max)
        {
            Assert.Equal(SpeedHack.MaxSpeed, SpeedHack.StepSpeed(1.0, 1.0, 100.0, 0.5, max), Eps);
        }

        [Fact]
        public void StepSpeed_SwapsInvertedBounds()
        {
            // 配置里把 min/max 写反了也要能用, 不能出现"怎么按都没反应"
            Assert.Equal(3.0, SpeedHack.StepSpeed(2.5, 1.0, 0.5, 4.0, 0.5), Eps);
            Assert.Equal(1.0, SpeedHack.StepSpeed(1.5, -1.0, 0.5, 4.0, 0.5), Eps);
        }

        [Fact]
        public void StepSpeed_IgnoresUnusableDelta()
        {
            Assert.Equal(2.0, SpeedHack.StepSpeed(2.0, 0.0), Eps);
            Assert.Equal(2.0, SpeedHack.StepSpeed(2.0, double.NaN), Eps);
            Assert.Equal(2.0, SpeedHack.StepSpeed(2.0, double.PositiveInfinity), Eps);
        }

        [Fact]
        public void StepSpeed_TreatsUnusableCurrentAsOneX()
        {
            Assert.Equal(1.5, SpeedHack.StepSpeed(double.NaN, 1.0), Eps);
            Assert.Equal(1.5, SpeedHack.StepSpeed(double.PositiveInfinity, 1.0), Eps);
        }

        [Fact]
        public void ClampSpeed_RoundsAwayFloatingPointNoise()
        {
            // 0.1 步进累加会飘成 2.5000000000000004 / 2.9999999999999996 这种数
            double speed = 2.0;
            for (int i = 0; i < 10; i++) speed = SpeedHack.StepSpeed(speed, 1.0, 0.1, 0.1, 10.0);
            Assert.Equal(3.0, speed, Eps);

            Assert.Equal(2.5, SpeedHack.ClampSpeed(2.5000000000000004), Eps);
            Assert.Equal(0.3, SpeedHack.ClampSpeed(0.30000000000000004), Eps);
        }

        [Fact]
        public void ClampSpeed_HandlesNaNInfinityAndBounds()
        {
            Assert.Equal(1.0, SpeedHack.ClampSpeed(double.NaN), Eps);
            Assert.Equal(1.0, SpeedHack.ClampSpeed(double.PositiveInfinity), Eps);
            Assert.Equal(SpeedHack.MinSpeed, SpeedHack.ClampSpeed(0.001), Eps);
            Assert.Equal(SpeedHack.MaxSpeed, SpeedHack.ClampSpeed(999.0), Eps);
            Assert.Equal(2.0, SpeedHack.ClampSpeed(2.5, 1.0, 2.0), Eps);
        }

        [Fact]
        public void Constants_MatchDocumentedDefaults()
        {
            Assert.Equal(0.1, SpeedHack.MinSpeed, Eps);
            Assert.Equal(10.0, SpeedHack.MaxSpeed, Eps);
            Assert.Equal(0.5, SpeedHack.DefaultSpeedStep, Eps);

            // 热键范围必须落在加载器 ap_speed_set 接受的 (0, 100] 之内
            Assert.True(SpeedHack.MinSpeed > 0.0);
            Assert.True(SpeedHack.MaxSpeed <= 100.0);
            Assert.True(SpeedHack.MinSpeed < SpeedHack.MaxSpeed);
        }

        // ---------------- 没有加载器时的安全降级 ----------------

        [Fact]
        public void WithoutLoader_ReportsUnavailableWithSafeDefaults()
        {
            // 测试宿主里 version.dll 没有 ap_speed_* 导出 -> 必须静默降级, 不抛异常
            Assert.False(SpeedHack.IsAvailable);
            Assert.Equal(1.0, SpeedHack.Speed, Eps);
        }

        [Fact]
        public void WithoutLoader_SetSpeedReturnsFalseInsteadOfThrowing()
        {
            Assert.False(SpeedHack.SetSpeed(2.0));
            Assert.False(SpeedHack.Reset());
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(100.0001)]
        [InlineData(double.NaN)]
        public void SetSpeed_RejectsOutOfRangeWithoutThrowing(double speed)
        {
            Assert.False(SpeedHack.SetSpeed(speed));
        }

        // ---------------- 权限展示 ----------------

        [Fact]
        public void SpeedHackPermission_IsShownAsSensitive()
        {
            // 权限不再门控调用, 但仍要能在 UI/工具里把"会改游戏时间流速"标出来
            Assert.True(Permissions.IsSensitive(ModPermission.SpeedHack));
            Assert.Contains("SpeedHack", Permissions.Names(ModPermission.SpeedHack));
            Assert.Contains("SpeedHack", Permissions.Describe(ModPermission.ReadGameState | ModPermission.SpeedHack));
        }
    }
}
