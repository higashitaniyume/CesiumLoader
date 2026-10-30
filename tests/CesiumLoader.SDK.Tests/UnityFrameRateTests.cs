using CesiumLoader.SDK.Engine;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 没有 Unity 运行时也必须安全降级: 读取返回 0, 写入返回 false, 不把 ECall 的
    /// JIT 失败抛出 SDK 边界(与 <c>docs/SDK-Unity调用与ECall隔离.md</c> 的契约一致)。
    /// </summary>
    public class UnityFrameRateTests
    {
        [Fact]
        public void Reads_DegradeToZero()
        {
            Assert.Equal(0, UnityFrameRate.Target);
            Assert.Equal(0, UnityFrameRate.VSync);
        }

        [Fact]
        public void Writes_ReturnFalse()
        {
            Assert.False(UnityFrameRate.SetUnlimited());
            Assert.False(UnityFrameRate.SetTarget(144));
            Assert.False(UnityFrameRate.SetVSync(0));
        }

        [Fact]
        public void RejectsOutOfRangeValuesBeforeTouchingUnity()
        {
            Assert.False(UnityFrameRate.SetTarget(0));
            Assert.False(UnityFrameRate.SetTarget(-2));
            Assert.False(UnityFrameRate.SetTarget(UnityFrameRate.MaxCustom + 1));
            Assert.False(UnityFrameRate.SetVSync(-1));
            Assert.False(UnityFrameRate.SetVSync(5));
        }

        [Fact]
        public void Describe_MentionsBothSettings()
        {
            string text = UnityFrameRate.Describe();
            Assert.Contains("targetFrameRate", text);
            Assert.Contains("vSyncCount", text);
        }
    }
}
