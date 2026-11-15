using CesiumLoader.SDK.Manifests;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>平台声明: [ModManifest(Platforms=...)] 位标记与 sidecar 的 platforms 数组。</summary>
    public class ModPlatformTests
    {
        [Fact]
        public void ToNames_KeepsKnownValuesInStableOrder()
        {
            Assert.Equal(new[] { "windows", "android" },
                ModPlatforms.ToNames(ModPlatform.Android | ModPlatform.Windows));
            Assert.Equal(new[] { "windows" }, ModPlatforms.ToNames(ModPlatform.Windows));
            Assert.Equal(new[] { "android" }, ModPlatforms.ToNames(ModPlatform.Android));
            Assert.Empty(ModPlatforms.ToNames(ModPlatform.None));
        }

        [Fact]
        public void All_IsBothPlatforms()
        {
            Assert.Equal(ModPlatform.Windows | ModPlatform.Android, ModPlatform.All);
            Assert.Equal(new[] { "windows", "android" }, ModPlatforms.ToNames(ModPlatform.All));
        }

        [Fact]
        public void Parse_KeepsKnownValuesAndDropsUnknown()
        {
            Assert.Equal(ModPlatform.Windows | ModPlatform.Android,
                ModPlatforms.Parse(new[] { "android", "windows", "android" }));
            Assert.Equal(ModPlatform.Windows, ModPlatforms.Parse(new[] { " Windows " }));
            Assert.Equal(ModPlatform.Android, ModPlatforms.Parse(new[] { "ANDROID" }));
            Assert.Equal(ModPlatform.None, ModPlatforms.Parse(null));
            Assert.Equal(ModPlatform.None, ModPlatforms.Parse(new string[0]));
            Assert.Equal(ModPlatform.None, ModPlatforms.Parse(new[] { "linux", "" }));
        }

        [Fact]
        public void Normalize_IsParsePlusToNames()
        {
            Assert.Equal(new[] { "windows", "android" },
                ModPlatforms.Normalize(new[] { "android", "windows", "android" }));
            Assert.Empty(ModPlatforms.Normalize(null));
            Assert.Empty(ModPlatforms.Normalize(new[] { "linux" }));
        }

        [Fact]
        public void Supports_TreatsUndeclaredAsBothPlatforms()
        {
            Assert.True(ModPlatforms.Supports(ModPlatform.None, "windows"));
            Assert.True(ModPlatforms.Supports(ModPlatform.None, "android"));
            // 已声明时只认这两种；认不出的平台名一律不支持(不是"未知就放行")
            Assert.False(ModPlatforms.Supports(ModPlatform.Windows, "linux"));
        }

        [Fact]
        public void Supports_UsesDeclaredFlags()
        {
            Assert.True(ModPlatforms.Supports(ModPlatform.Windows, "windows"));
            Assert.False(ModPlatforms.Supports(ModPlatform.Windows, "android"));
            Assert.True(ModPlatforms.Supports(ModPlatform.All, "android"));
            Assert.True(ModPlatforms.Supports(ModPlatform.All, "WINDOWS"));
        }

        [Fact]
        public void Manifest_PlatformsDefaultToUndeclared()
        {
            var m = new ModManifestAttribute("MyMod");

            Assert.Equal(ModPlatform.None, m.Platforms);
        }

        [Fact]
        public void Serialize_OmitsPlatformsWhenUndeclared()
        {
            var json = SdkManifest.Serialize(new ModManifestAttribute("MyMod"));

            Assert.DoesNotContain("platforms", json);
        }

        [Fact]
        public void Serialize_WritesPlatformsWhenDeclared()
        {
            var manifest = new ModManifestAttribute("MyMod")
            {
                Platforms = ModPlatform.Android | ModPlatform.Windows,
            };

            var json = SdkManifest.Serialize(manifest);

            Assert.Contains("\"platforms\":[\"windows\",\"android\"]", json);
        }

        [Fact]
        public void Serialize_WritesSinglePlatform()
        {
            var manifest = new ModManifestAttribute("MyMod") { Platforms = ModPlatform.Windows };

            var json = SdkManifest.Serialize(manifest);

            Assert.Contains("\"platforms\":[\"windows\"]", json);
        }
    }
}
