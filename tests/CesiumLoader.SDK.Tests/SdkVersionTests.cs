using System.Reflection;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>SDK 版本协商语义(必须保持兼容, 不能随意改)。</summary>
    public class SdkVersionTests
    {
        [Fact]
        public void Current_MatchesReleaseLine()
        {
            // 本次扩展保持向后兼容 → 不升 major。发布时三处版本号同步递增:
            // SdkVersion.Current / SdkVersion.LoaderVersion / CesiumLoader.SDK.csproj 的 <Version>。
            Assert.Equal("2.1.6", SdkVersion.Current);
            Assert.Equal("2.1.6", SdkVersion.LoaderVersion);
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
            // 与当前版本相同 → 兼容（SDK 2.1.6 接受声明 2.1.6 的 mod）
            Assert.True(SdkVersion.Accepts(SdkVersion.Current));
            Assert.True(SdkVersion.Accepts("2.0.0"));
            Assert.True(SdkVersion.Accepts("1.0.0"));
            Assert.True(SdkVersion.Accepts("2.0.0-beta"));
        }

        [Fact]
        public void Accepts_RejectsNewerRequirement()
        {
            // 比当前版本更高(修订号/次版本/主版本) → 拒绝加载该 mod
            // 三个"更高版本"都从当前版本推导, 免得每次升版本号都要回来改这几行
            Assert.False(SdkVersion.Accepts(Bump(SdkVersion.Current, 2)));
            Assert.False(SdkVersion.Accepts(Bump(SdkVersion.Current, 1)));
            Assert.False(SdkVersion.Accepts(Bump(SdkVersion.Current, 0)));
        }

        /// <summary>把指定段 +1、更低的段归零(2.1.6 的 revision → 2.1.7, minor → 2.2.0, major → 3.0.0)。</summary>
        private static string Bump(string version, int index)
        {
            var parts = version.Split('.');
            var numbers = new int[3];
            for (int i = 0; i < 3 && i < parts.Length; i++)
                int.TryParse(parts[i], out numbers[i]);

            numbers[index]++;
            for (int i = index + 1; i < 3; i++)
                numbers[i] = 0;

            return numbers[0] + "." + numbers[1] + "." + numbers[2];
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
