using System;
using System.Reflection;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>ModManifest / ModDependency / 权限类别 / SdkInfo 元数据读取。</summary>
    public class ModManifestTests
    {
        [Fact]
        public void Manifest_Defaults_AreUsable()
        {
            var m = new ModManifestAttribute("MyMod");

            Assert.Equal("MyMod", m.Name);
            Assert.Equal("1.0.0", m.Version);
            Assert.Equal("", m.Author);
            Assert.Equal("", m.Description);
            Assert.Equal(ModPermission.None, m.Permissions);
            Assert.Null(m.SdkVersion);
            Assert.NotNull(m.Dependencies);
            Assert.Empty(m.Dependencies);
        }

        [Fact]
        public void Manifest_KeepsDeclaredValues()
        {
            var m = new ModManifestAttribute("MyMod", "1.2.3", "作者", "描述")
            {
                Permissions = ModPermission.ReadGameState | ModPermission.Camera,
                SdkVersion = "2.0.0",
                Dependencies = new[] { new ModDependency("Other", "1.0.0") },
            };

            Assert.Equal("1.2.3", m.Version);
            Assert.Equal("作者", m.Author);
            Assert.Equal("描述", m.Description);
            Assert.Equal("2.0.0", m.SdkVersion);
            Assert.Single(m.Dependencies);
            Assert.Equal("Other", m.Dependencies[0].Id);
            Assert.Equal("1.0.0", m.Dependencies[0].MinVersion);
        }

        [Fact]
        public void ModDependency_MinVersionIsOptional()
        {
            var dep = new ModDependency("Any");

            Assert.Equal("Any", dep.Id);
            Assert.Null(dep.MinVersion);
        }

        [Fact]
        public void Permission_LegacyBitValues_AreUnchanged()
        {
            // 位值兼容性: 旧加载器/旧工具读到的仍是它们认识的那几个位
            Assert.Equal(1, (int)ModPermission.ReadGameState);
            Assert.Equal(2, (int)ModPermission.GameActions);
            Assert.Equal(4, (int)ModPermission.SpeedHack);
            Assert.Equal(8, (int)ModPermission.FileWrite);
        }

        [Fact]
        public void Permission_NewCategories_AreDistinctPowersOfTwo()
        {
            var added = new[]
            {
                ModPermission.ModifyGameState,
                ModPermission.Camera,
                ModPermission.Input,
                ModPermission.UI,
                ModPermission.FileSystem,
                ModPermission.Network,
                ModPermission.Debug,
            };

            foreach (var perm in added)
            {
                int v = (int)perm;
                Assert.True(v > 8, perm + " 应使用高位以免与旧值冲突");
                Assert.Equal(0, v & (v - 1));   // 2 的幂
            }

            Assert.Equal(added.Length, Permissions.All().Length - 4);
        }

        [Fact]
        public void Permission_Names_AreStableAndOrdered()
        {
            var names = Permissions.Names(ModPermission.ReadGameState | ModPermission.Camera | ModPermission.Debug);

            Assert.Equal(new[] { "ReadGameState", "Camera", "Debug" }, names);
            Assert.Equal("ReadGameState, Camera, Debug",
                Permissions.Describe(ModPermission.ReadGameState | ModPermission.Camera | ModPermission.Debug));
            Assert.Equal("None", Permissions.Describe(ModPermission.None));
        }

        [Fact]
        public void Permission_IsSensitive_FlagsStateChangingCaps()
        {
            Assert.True(Permissions.IsSensitive(ModPermission.GameActions));
            Assert.True(Permissions.IsSensitive(ModPermission.SpeedHack));
            Assert.True(Permissions.IsSensitive(ModPermission.ModifyGameState));
            Assert.True(Permissions.IsSensitive(ModPermission.Network));

            Assert.False(Permissions.IsSensitive(ModPermission.ReadGameState));
            Assert.False(Permissions.IsSensitive(ModPermission.Camera));
            Assert.False(Permissions.IsSensitive(ModPermission.Debug));
            Assert.False(Permissions.IsSensitive(ModPermission.None));
        }

        [Fact]
        public void Permissions_AreNoLongerGating()
        {
            // 权限机制已取消: Has/Require 必须恒为 true(否则旧 mod 会突然失效)
            Assert.True(Permissions.Has(ModPermission.SpeedHack));
            Assert.True(Permissions.Has(ModPermission.None));
            Assert.True(Permissions.Require(ModPermission.GameActions, "GameActions.Roll"));
        }

        [Fact]
        public void SdkInfo_ManifestOf_TestAssemblyFallsBackToAssemblyName()
        {
            var manifest = SdkInfo.ManifestOf(typeof(ModManifestTests).Assembly);

            Assert.NotNull(manifest);
            Assert.Equal(typeof(ModManifestTests).Assembly.GetName().Name, manifest.Name);
        }

        [Fact]
        public void SdkInfo_ManifestOf_NullAssembly_IsSafe()
        {
            var manifest = SdkInfo.ManifestOf(null);

            Assert.NotNull(manifest);
            Assert.Equal("Unknown", manifest.Name);
        }

        [Fact]
        public void SdkInfo_CallingAssembly_IsNotTheSdkItself()
        {
            Assembly calling = SdkInfo.CallingAssembly();

            Assert.NotNull(calling);
            Assert.NotSame(typeof(SdkInfo).Assembly, calling);
        }
    }
}
