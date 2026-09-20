using System.Reflection;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>SDK 版本协商语义(必须保持兼容, 不能随意改)。</summary>
    public class SdkVersionTests
    {
        [Fact]
        public void Current_IsTwoZeroZero()
        {
            // 本次扩展保持向后兼容 → 不升 major
            Assert.Equal("2.0.0", SdkVersion.Current);
            Assert.Equal("2.0.0", SdkVersion.LoaderVersion);
        }

        [Theory]
        [InlineData("1.0.0", 2, 0, 0, -1)]
        [InlineData("2.0.0", 2, 0, 0, 0)]
        [InlineData("2.0.1", 2, 0, 0, 1)]
        [InlineData("1.9.9", 2, 0, 0, -1)]
        [InlineData("2.1.0", 2, 0, 0, 1)]
        public void Compare_OrdersByMajorMinorPatch(string a, int major, int minor, int patch, int expected)
        {
            string b = major + "." + minor + "." + patch;

            Assert.Equal(expected, SdkVersion.Compare(a, b));
            Assert.Equal(-expected, SdkVersion.Compare(b, a));
        }

        [Fact]
        public void Compare_SkipsPreReleaseSuffixAndHandlesGarbage()
        {
            Assert.Equal(0, SdkVersion.Compare("2.0.0-beta.1", "2.0.0"));
            Assert.Equal(1, SdkVersion.Compare("2.0.0", "1.9.9"));
            // 非法输入按 0 处理, 不抛异常
            Assert.Equal(0, SdkVersion.Compare("abc", "0.0.0"));
            Assert.Equal(0, SdkVersion.Compare(null, ""));
        }

        [Fact]
        public void Accepts_AllowsSameOrOlderDeclaration()
        {
            Assert.True(SdkVersion.Accepts("2.0.0"));
            Assert.True(SdkVersion.Accepts("1.0.0"));
            Assert.True(SdkVersion.Accepts("2.0.0-beta"));
        }

        [Fact]
        public void Accepts_RejectsNewerRequirement()
        {
            Assert.False(SdkVersion.Accepts("2.0.1"));
            Assert.False(SdkVersion.Accepts("3.0.0"));
        }

        [Fact]
        public void Accepts_TreatsMissingDeclarationAsCompatible()
        {
            Assert.True(SdkVersion.Accepts(null));
            Assert.True(SdkVersion.Accepts(""));
        }

        [Fact]
        public void DeclaredAtLeast_RequiresDeclaration()
        {
            // 测试程序集没有 [assembly: ModManifest], 因此视为"未声明" → false
            Assembly asm = typeof(SdkVersionTests).Assembly;

            Assert.False(SdkVersion.DeclaredAtLeast(asm, "1.0.0"));
        }
    }
}
