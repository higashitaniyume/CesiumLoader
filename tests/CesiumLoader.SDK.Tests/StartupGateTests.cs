using CesiumLoader.SDK.Mods;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 启动门判定: 这里只覆盖纯逻辑(不碰 Unity / UniTask)。
    ///
    /// 语义约定(与 ModHost 的旧行为兼容):
    ///  - 游戏就绪 → 立即初始化(GameReady);
    ///  - 未就绪 → 最多等满上限(Timeout), 任何 mod 都不会比改动前更晚初始化;
    ///  - 上限 &lt;= 0 → 调用方显式要求不等待(Disabled)。
    /// </summary>
    public class StartupGateTests
    {
        public StartupGateTests()
        {
            StartupGate.ResetForTesting();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-30000)]
        public void Decide_DisabledWhenCapIsNotPositive(int capMs)
        {
            Assert.Equal(StartupOutcome.Disabled, StartupGate.Decide(false, 0, capMs));
            Assert.Equal(StartupOutcome.Disabled, StartupGate.Decide(true, 999999, capMs));
        }

        [Fact]
        public void Decide_GameReadyWinsEvenAfterCap()
        {
            // 就绪判定晚于上限也要按就绪处理(上限只是兜底, 不阻止就绪)
            Assert.Equal(StartupOutcome.GameReady, StartupGate.Decide(true, 40000, 30000));
        }

        [Fact]
        public void Decide_PendingBeforeCap()
        {
            Assert.Equal(StartupOutcome.Pending, StartupGate.Decide(false, 0, 30000));
            Assert.Equal(StartupOutcome.Pending, StartupGate.Decide(false, 29999, 30000));
        }

        [Fact]
        public void Decide_TimeoutAtAndAfterCap()
        {
            Assert.Equal(StartupOutcome.Timeout, StartupGate.Decide(false, 30000, 30000));
            Assert.Equal(StartupOutcome.Timeout, StartupGate.Decide(false, 30001, 30000));
        }

        [Fact]
        public void Decide_NegativeElapsedIsNotTreatedAsTimeout()
        {
            // 计时源异常(返回 0/负)时不应该提前触发超时
            Assert.Equal(StartupOutcome.Pending, StartupGate.Decide(false, -5, 30000));
        }

        [Fact]
        public void Enabled_DefaultsToTrue()
        {
            Assert.True(StartupGate.Enabled);
        }

        [Fact]
        public void Enabled_CanBeTurnedOff()
        {
            StartupGate.Enabled = false;
            Assert.False(StartupGate.Enabled);

            StartupGate.Enabled = true;
            Assert.True(StartupGate.Enabled);
        }

        [Fact]
        public void Defaults_AreTheDocumentedValues()
        {
            Assert.Equal(100, StartupGate.PollIntervalMs);
            Assert.Equal(1000, StartupGate.SettleMs);
        }

        [Fact]
        public void MarkReady_LatchesAndRecordsWait()
        {
            Assert.False(StartupGate.IsGameReady);
            Assert.Equal(-1, StartupGate.ReadyAtMs);

            StartupGate.MarkReady(1234);

            Assert.True(StartupGate.IsGameReady);
            Assert.Equal(1234, StartupGate.ReadyAtMs);

            // 锁存: 第二次上报不覆盖第一次的实际等待时长
            StartupGate.MarkReady(9999);
            Assert.Equal(1234, StartupGate.ReadyAtMs);
        }

        [Fact]
        public void ResetForTesting_ClearsLatch()
        {
            StartupGate.MarkReady(500);
            StartupGate.Enabled = false;

            StartupGate.ResetForTesting();

            Assert.False(StartupGate.IsGameReady);
            Assert.Equal(-1, StartupGate.ReadyAtMs);
            Assert.True(StartupGate.Enabled);
        }
    }
}
