using System;
using System.Globalization;
using System.IO;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 变速 (SpeedHack)。分两块:
    ///  - 热键用的纯计算(StepSpeed / ClampSpeed): 完全离线, 覆盖边界与脏配置;
    ///  - 控制文件通道: 本游戏的热更程序集无法 P/Invoke, SpeedHack 通过加载器的
    ///    request.txt / state.txt 通信 —— 用临时目录完整覆盖"可用/不可用/写盘/区域"。
    /// 所有涉及通道的用例都自带临时目录 + 重置缓存, 绝不读真实游戏目录。
    /// </summary>
    public class SpeedHackTests : IDisposable
    {
        private const double Eps = 1e-9;

        private readonly string _dir;

        public SpeedHackTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cesium-speedhack-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            SpeedHack.DirectoryOverride = _dir;
            SpeedHack.ResetCache();
        }

        public void Dispose()
        {
            SpeedHack.DirectoryOverride = null;
            SpeedHack.ResetCache();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string StatePath => Path.Combine(_dir, "state.txt");
        private string RequestPath => Path.Combine(_dir, "request.txt");

        private void WriteState(string text) => File.WriteAllText(StatePath, text);

        private void WriteHealthyState(string speed = "2.000") =>
            WriteState("version=2.1.5\r\nspeed=" + speed + "\r\nbase=2.000\r\nactive=1\r\nhooks=4\r\n");

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
            // 下界即使配置成 0.5 也会被抬到硬下限 1.0(不允许减速)
            Assert.Equal(1.0, SpeedHack.StepSpeed(0.6, -1.0, 0.5, 0.5, 4.0), Eps);
        }

        [Fact]
        public void StepSpeed_AtLimitReturnsSameValue()
        {
            // 顶到极限时返回值不变 -> 调用方据此提示"已是极限", 不必写盘
            Assert.Equal(4.0, SpeedHack.StepSpeed(4.0, 1.0, 0.5, 0.5, 4.0), Eps);
            Assert.Equal(1.0, SpeedHack.StepSpeed(0.5, -1.0, 0.5, 0.5, 4.0), Eps);
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
            for (int i = 0; i < 10; i++) speed = SpeedHack.StepSpeed(speed, 1.0, 0.1, 1.0, 10.0);
            Assert.Equal(3.0, speed, Eps);

            Assert.Equal(2.5, SpeedHack.ClampSpeed(2.5000000000000004), Eps);
            Assert.Equal(1.5, SpeedHack.ClampSpeed(1.5000000000000002), Eps);
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

        // 硬规则: 不允许减速。传进来的 min 再小也会被抬到 1.0。
        [Fact]
        public void ClampSpeed_NeverGoesBelowOneEvenIfMinAsksForIt()
        {
            Assert.Equal(1.0, SpeedHack.ClampSpeed(0.5, 0.1, 10.0), Eps);
            Assert.Equal(1.0, SpeedHack.ClampSpeed(0.5, 0.0, 10.0), Eps);
            Assert.Equal(1.0, SpeedHack.ClampSpeed(1.0, 0.1, 10.0), Eps);
            Assert.Equal(1.0, SpeedHack.ClampSpeed(double.NaN, 0.1, 10.0), Eps);
            // min > max 写反 + 都低于下限: 仍然是 1.0, 不会反过来夹出奇怪的值
            Assert.Equal(1.0, SpeedHack.ClampSpeed(0.5, 5.0, 0.1), Eps);
        }

        [Fact]
        public void StepSpeed_StopsAtOneWhenSlowingDown()
        {
            // 1.0 再往下调: 返回原值 = 调用方据此提示"已到极限", 不写盘
            Assert.Equal(1.0, SpeedHack.StepSpeed(1.0, -1.0, 0.5, 1.0, 10.0), Eps);
            Assert.Equal(1.0, SpeedHack.StepSpeed(1.0, -1.0, 0.5, 0.1, 10.0), Eps);   // 传 0.1 也一样
            Assert.Equal(1.5, SpeedHack.StepSpeed(1.0, +1.0, 0.5, 1.0, 10.0), Eps);
        }

        [Fact]
        public void Constants_MatchDocumentedDefaults()
        {
            // 下限必须是 1.0: "不允许减速"是产品规则, 不是建议值
            Assert.Equal(1.0, SpeedHack.MinSpeed, Eps);
            Assert.Equal(10.0, SpeedHack.MaxSpeed, Eps);
            Assert.Equal(0.5, SpeedHack.DefaultSpeedStep, Eps);

            // 热键范围必须落在控制文件协议/引擎接受的 [1, 100] 之内
            Assert.True(SpeedHack.MinSpeed >= 1.0);
            Assert.True(SpeedHack.MaxSpeed <= SpeedHack.HardMaxSpeed);
            Assert.Equal(100.0, SpeedHack.HardMaxSpeed, Eps);
            Assert.True(SpeedHack.MinSpeed < SpeedHack.MaxSpeed);
        }

        // 低于 1 倍的请求连文件都不该写 —— 这是 SDK 这道闸的直接证据。
        [Fact]
        public void SetSpeed_RejectsAnythingBelowOne()
        {
            WriteHealthyState();
            Assert.False(File.Exists(RequestPath));   // 前置: 还没有请求文件

            Assert.False(SpeedHack.SetSpeed(0.5));
            Assert.False(SpeedHack.SetSpeed(0.999));
            Assert.False(SpeedHack.SetSpeed(0.0));
            Assert.False(SpeedHack.SetSpeed(-1.0));

            // 被拒绝 = 一个字节都不写(不能只靠加载器兜底, 这里就要拦住)
            Assert.False(File.Exists(RequestPath));

            // 1.0 本身是合法的(下限)
            Assert.True(SpeedHack.SetSpeed(1.0));
            Assert.Equal("1.000", File.ReadAllText(RequestPath).Trim());
        }

        // ---------------- 控制文件通道: 可用性判定 ----------------

        [Fact]
        public void WithoutStateFile_ReportsUnavailableWithSafeDefaults()
        {
            // 加载器没起(或版本过旧没有通道) -> 必须静默降级, 不抛异常
            Assert.False(SpeedHack.IsAvailable);
            Assert.Equal(1.0, SpeedHack.Speed, Eps);
            Assert.NotNull(SpeedHack.UnavailableReason);
            Assert.Contains("不可用", SpeedHack.Describe());
        }

        [Fact]
        public void HealthyStateFile_MakesEngineAvailable()
        {
            WriteHealthyState("2.500");

            Assert.True(SpeedHack.IsAvailable);
            Assert.Equal(2.5, SpeedHack.Speed, Eps);
            Assert.Equal(2.0, SpeedHack.BaseSpeed, Eps);
            Assert.Null(SpeedHack.UnavailableReason);
            Assert.Contains("2.5", SpeedHack.Describe());
        }

        // 状态文件的数值解析是手写的(不用 double.TryParse 的重载 —— 热更程序集里 BCL 可能缺失)。
        // 这里按加载器真实写出的格式覆盖正常值([1, 100] 区间内)。
        [Theory]
        [InlineData("2.500", 2.5)]
        [InlineData("4", 4.0)]
        [InlineData("1.000", 1.0)]
        [InlineData("+3.000", 3.0)]
        [InlineData("10.000", 10.0)]
        [InlineData(" 1.500 ", 1.5)]
        public void Speed_ParsesLoaderDecimalFormat(string text, double expected)
        {
            WriteState("version=2.1.5\r\nspeed=" + text + "\r\nbase=1.000\r\nactive=1\r\nhooks=4\r\n");

            Assert.True(SpeedHack.IsAvailable);
            Assert.Equal(expected, SpeedHack.Speed, Eps);
        }

        [Theory]
        [InlineData("nan")]
        [InlineData("inf")]
        [InlineData("2.5.5")]
        [InlineData("1e5")]
        [InlineData("0x2")]
        [InlineData("")]
        [InlineData("-2.000")]
        [InlineData("999.000")]
        [InlineData("2,5")]
        [InlineData("0.500")]   // 低于 1 倍: 旧加载器/外物写的值也不能让 mod 以为在减速
        [InlineData("0.100")]
        public void Speed_UnparsableValueFallsBackToNormalSpeed(string text)
        {
            WriteState("version=2.1.5\r\nspeed=" + text + "\r\nbase=1.000\r\nactive=1\r\nhooks=4\r\n");

            // 解析不了 -> 1.0(默认值) 且绝不抛异常
            Assert.Equal(1.0, SpeedHack.Speed, Eps);
        }

        [Fact]
        public void ZeroHooks_ReportsUnavailable()
        {
            WriteState("version=2.1.5\r\nspeed=2.000\r\nbase=2.000\r\nactive=0\r\nhooks=0\r\n");

            // hook 一个都没装上 -> 即便能写文件也变不了速, 必须报不可用
            Assert.False(SpeedHack.IsAvailable);
        }

        [Fact]
        public void StateFileWithoutHooksKey_ReportsUnavailable()
        {
            WriteState("version=2.1.5\r\nspeed=2.000\r\n");

            Assert.False(SpeedHack.IsAvailable);
        }

        [Fact]
        public void BrokenStateFile_DoesNotThrow()
        {
            // 写了一半/被占用/不是 key=value: 一律当不可用, 不抛异常
            WriteState("这不是一个状态文件\n\x0\x0 ==");

            Assert.False(SpeedHack.IsAvailable);
            Assert.Equal(1.0, SpeedHack.Speed, Eps);
            Assert.Equal(1.0, SpeedHack.BaseSpeed, Eps);
        }

        [Fact]
        public void DirectoryOverride_WinsOverEnvironment()
        {
            Assert.Equal(_dir, SpeedHack.Directory);
        }

        // 这条用例锁住的是线上踩过的坑: 加载器写 state.txt 和 mod 初始化有先后差异,
        // 若把"不可用"缓存下来, 热键就会在整局游戏里永久失效(按了没反应, 日志还有误导性的原因)。
        [Fact]
        public void StateFileAppearingLater_MakesEngineAvailableWithoutResetCache()
        {
            Assert.False(SpeedHack.IsAvailable);
            Assert.NotNull(SpeedHack.UnavailableReason);

            WriteHealthyState("3.000");   // 加载器晚一步写出状态文件

            Assert.True(SpeedHack.IsAvailable);          // 不调用 ResetCache 也必须自愈
            Assert.Equal(3.0, SpeedHack.Speed, Eps);
            Assert.Null(SpeedHack.UnavailableReason);
        }

        [Fact]
        public void EngineDisappearingLater_ReportsUnavailableAgain()
        {
            WriteHealthyState();
            Assert.True(SpeedHack.IsAvailable);

            File.Delete(StatePath);                       // 加载器挂了/文件被删

            Assert.False(SpeedHack.IsAvailable);
            Assert.Equal(1.0, SpeedHack.Speed, Eps);
        }

        [Fact]
        public void SpeedDirEnvironmentVariable_IsUsedWhenNoOverride()
        {
            WithNoOverride(() =>
            {
                using (var env = new EnvScope("CESIUM_SPEED_DIR", _dir))
                using (new EnvScope("CESIUM_LOG_DIR", null))
                {
                    WriteHealthyState("2.500");

                    Assert.Equal(_dir, SpeedHack.Directory);
                    Assert.True(SpeedHack.IsAvailable);
                    Assert.Equal(2.5, SpeedHack.Speed, Eps);
                }
            });
        }

        [Fact]
        public void LogDirSibling_IsUsedWhenSpeedDirIsUnreadable()
        {
            WithNoOverride(() =>
            {
                // 只给 CESIUM_LOG_DIR(加载器的 <loader>\logs): 控制目录应能推导出 <loader>\speed
                string logDir = Path.Combine(_dir, "logs");
                string speedDir = Path.Combine(_dir, "speed");
                Directory.CreateDirectory(logDir);
                Directory.CreateDirectory(speedDir);
                File.WriteAllText(Path.Combine(speedDir, "state.txt"),
                    "version=2.1.5\r\nspeed=4.000\r\nbase=1.000\r\nactive=1\r\nhooks=4\r\n");

                using (new EnvScope("CESIUM_SPEED_DIR", null))
                using (new EnvScope("CESIUM_LOG_DIR", logDir))
                {
                    Assert.Equal(speedDir, SpeedHack.Directory);
                    Assert.True(SpeedHack.IsAvailable);
                    Assert.Equal(4.0, SpeedHack.Speed, Eps);
                }
            });
        }

        [Fact]
        public void Refresh_ClearsCachedDirectory()
        {
            WriteHealthyState("2.000");
            Assert.Equal(2.0, SpeedHack.Speed, Eps);

            // 换一个目录(加载器重装/换位置), 覆盖目录指向新位置
            string other = Path.Combine(Path.GetTempPath(), "cesium-speedhack-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(other);
            try
            {
                File.WriteAllText(Path.Combine(other, "state.txt"),
                    "version=2.1.5\r\nspeed=5.000\r\nbase=1.000\r\nactive=1\r\nhooks=4\r\n");

                SpeedHack.DirectoryOverride = other;
                SpeedHack.Refresh();

                Assert.Equal(other, SpeedHack.Directory);
                Assert.Equal(5.0, SpeedHack.Speed, Eps);
            }
            finally
            {
                SpeedHack.DirectoryOverride = _dir;
                SpeedHack.ResetCache();
                try { Directory.Delete(other, true); } catch { }
            }
        }

        [Fact]
        public void Diagnostics_ListsCandidatesAndEnvironment()
        {
            WriteHealthyState();

            string report = SpeedHack.Diagnostics();

            Assert.Contains("CESIUM_SPEED_DIR", report);
            Assert.Contains(_dir, report);
            Assert.Contains("[有state]", report);
            Assert.Contains(SpeedHack.Directory, report);
        }

        // 只剩 LocalAppData 镜像(%LOCALAPPDATA%\AstralParty_ModLoader\speed)时也必须能定位 ——
        // 这是"环境变量读不到 / 路径推导都不可用"时的最后一道保险(加载器会同时写这一份)。
        [Fact]
        public void LocalAppDataMirror_IsUsedWhenEverythingElseIsMissing()
        {
            WithNoOverride(() =>
            {
                string mirror = Path.Combine(_dir, "mirror");
                string speedDir = Path.Combine(mirror, "AstralParty_ModLoader", "speed");
                Directory.CreateDirectory(speedDir);
                File.WriteAllText(Path.Combine(speedDir, "state.txt"),
                    "version=2.1.5\r\nspeed=6.000\r\nbase=1.000\r\nactive=1\r\nhooks=4\r\n");

                using (new EnvScope("CESIUM_SPEED_DIR", null))
                using (new EnvScope("CESIUM_LOG_DIR", null))
                using (new EnvScope("CESIUM_MODS_DIR", null))
                using (new EnvScope("CESIUM_SDK_DIR", null))
                using (new EnvScope("LOCALAPPDATA", mirror))
                {
                    Assert.Equal(speedDir, SpeedHack.Directory);
                    Assert.True(SpeedHack.IsAvailable);
                    Assert.Equal(6.0, SpeedHack.Speed, Eps);
                }
            });
        }

        // 读取失败必须写清楚"卡在哪一步" —— 不能再出现"文件不存在"这种把多种原因混在一起的提示。
        [Fact]
        public void Diagnostics_ReportsWhyTheStateFileCouldNotBeRead()
        {
            // 放一个"同名目录"顶替 state.txt: File.Exists 为 false, 打开也会失败
            Directory.CreateDirectory(StatePath);

            Assert.False(SpeedHack.IsAvailable);

            string report = SpeedHack.Diagnostics();
            Assert.Contains("读取失败于", report);
            Assert.Contains(StatePath, report);
        }

        [Fact]
        public void Diagnostics_NeverThrowsEvenWithNoEnvironmentAtAll()
        {
            WithNoOverride(() =>
            {
                using (new EnvScope("CESIUM_SPEED_DIR", null))
                using (new EnvScope("CESIUM_LOG_DIR", null))
                using (new EnvScope("CESIUM_MODS_DIR", null))
                using (new EnvScope("CESIUM_SDK_DIR", null))
                using (new EnvScope("LOCALAPPDATA", null))
                {
                    // 诊断/候选枚举是排查手段, 自己绝不能抛异常
                    var candidates = SpeedHack.Candidates();
                    Assert.NotNull(candidates);
                    Assert.NotEmpty(candidates);

                    string report = SpeedHack.Diagnostics();
                    Assert.False(string.IsNullOrEmpty(report));
                }
            });
        }

        // -------- 环境变量: 用完还原, 避免影响其它用例 --------

        private void WithNoOverride(Action body)
        {
            SpeedHack.DirectoryOverride = null;
            SpeedHack.ResetCache();
            try { body(); }
            finally
            {
                SpeedHack.DirectoryOverride = _dir;
                SpeedHack.ResetCache();
            }
        }

        private sealed class EnvScope : IDisposable
        {
            private readonly string _name;
            private readonly string _old;

            public EnvScope(string name, string value)
            {
                _name = name;
                _old = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            public void Dispose() { Environment.SetEnvironmentVariable(_name, _old); }
        }

        // ---------------- 控制文件通道: 写请求 ----------------

        [Fact]
        public void SetSpeed_WritesRequestFileWhenAvailable()
        {
            WriteHealthyState();

            Assert.True(SpeedHack.SetSpeed(2.5));
            Assert.True(File.Exists(RequestPath));

            string text = File.ReadAllText(RequestPath).Trim();
            Assert.Equal("2.500", text);

            // 1.0 = 关闭变速, 也必须能写进去(热键"关"就是它)
            Assert.True(SpeedHack.Reset());
            Assert.Equal("1.000", File.ReadAllText(RequestPath).Trim());
        }

        [Fact]
        public void SetSpeed_WritesInvariantDecimalPointEvenWithCommaCulture()
        {
            // 中文/德语等区域的小数点是逗号; 原生侧只认点号 -> 必须用不变文化写盘
            WriteHealthyState();
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.True(SpeedHack.SetSpeed(2.5));
                Assert.Equal("2.500", File.ReadAllText(RequestPath).Trim());
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void SetSpeed_WhenUnavailable_ReportsFalseAndWritesNothing()
        {
            Assert.False(SpeedHack.SetSpeed(2.0));
            Assert.False(SpeedHack.Reset());
            Assert.False(File.Exists(RequestPath));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(100.0001)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void SetSpeed_RejectsOutOfRangeWithoutThrowing(double speed)
        {
            WriteHealthyState();

            Assert.False(SpeedHack.SetSpeed(speed));
            Assert.False(File.Exists(RequestPath));   // 非法值绝不能落到盘上
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
