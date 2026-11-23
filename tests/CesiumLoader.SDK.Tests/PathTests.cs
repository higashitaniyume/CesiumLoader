using System;
using System.IO;
using Xunit;
using SdkIOPath = CesiumLoader.SDK.IO.Path;
using CesiumLoader.SDK.IO;
using CesiumLoader.SDK;

namespace CesiumLoader.SDK.Tests
{
    public sealed class PathTests
    {
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

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(_name, _old);
            }
        }

        [Fact]
        public void EnvironmentVariables_TakePrecedence_ForLoaderAndGameDirectory()
        {
            string fakeGame = @"C:\Games\AstralParty";
            string fakeLoader = @"C:\Games\AstralParty\AstralParty_ModLoader";

            using (new EnvScope("CESIUM_GAME_DIR", fakeGame))
            using (new EnvScope("CESIUM_LOADER_DIR", fakeLoader))
            using (new EnvScope("CESIUM_LOG_DIR", null))
            using (new EnvScope("CESIUM_MODS_DIR", null))
            {
                Assert.Equal(fakeLoader, SdkIOPath.LoaderDirectory);
                Assert.Equal(fakeLoader, SdkIOPath.LoaderRootDirectory);
                Assert.Equal(fakeGame, SdkIOPath.GameDirectory);
                Assert.Equal(fakeGame, SdkIOPath.GameRootDirectory);

                // Subdirectories
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "mods"), SdkIOPath.ModsDirectory);
                // 4.0.0 起 configs 已废弃（加载器启动时改名 configs.bak），属性只为兼容保留。
#pragma warning disable CS0618
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "configs"), SdkIOPath.ConfigsDirectory);
#pragma warning restore CS0618
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "logs"), SdkIOPath.LogsDirectory);
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "skins"), SdkIOPath.SkinsDirectory);
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "audio"), SdkIOPath.AudioDirectory);
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "audio-schemes"), SdkIOPath.AudioSchemesDirectory);
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "speed"), SdkIOPath.SpeedDirectory);
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "window"), SdkIOPath.WindowDirectory);
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "sdk"), SdkIOPath.SdkDirectory);
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "bootstrap"), SdkIOPath.BootstrapDirectory);
            }
        }

        [Fact]
        public void LoaderDirectory_DerivedFromModsDir_WhenLoaderDirNotSet()
        {
            string fakeGame = @"D:\SteamLibrary\steamapps\common\Astral Party\game";
            string fakeLoader = System.IO.Path.Combine(fakeGame, "AstralParty_ModLoader");
            string fakeMods = System.IO.Path.Combine(fakeLoader, "mods");

            using (new EnvScope("CESIUM_GAME_DIR", null))
            using (new EnvScope("CESIUM_LOADER_DIR", null))
            using (new EnvScope("CESIUM_MODS_DIR", fakeMods))
            using (new EnvScope("CESIUM_LOG_DIR", null))
            using (new EnvScope("CESIUM_SDK_DIR", null))
            {
                Assert.Equal(fakeLoader, SdkIOPath.LoaderDirectory);
                Assert.Equal(fakeGame, SdkIOPath.GameDirectory);
                Assert.Equal(fakeMods, SdkIOPath.ModsDirectory);
            }
        }

        [Fact]
        public void SubpathGetters_CombineCorrectly()
        {
            string fakeGame = @"C:\Games\AstralParty";
            string fakeLoader = @"C:\Games\AstralParty\AstralParty_ModLoader";

            using (new EnvScope("CESIUM_GAME_DIR", fakeGame))
            using (new EnvScope("CESIUM_LOADER_DIR", fakeLoader))
            using (new EnvScope("CESIUM_MODS_DIR", null))
            {
                Assert.Equal(System.IO.Path.Combine(fakeGame, "AstralParty_CN.exe"), SdkIOPath.GetGamePath("AstralParty_CN.exe"));
                Assert.Equal(System.IO.Path.Combine(fakeGame, "logs", "game.log"), SdkIOPath.GetGamePath("logs", "game.log"));

                Assert.Equal(System.IO.Path.Combine(fakeLoader, "version.dll"), SdkIOPath.GetLoaderPath("version.dll"));

                Assert.Equal(System.IO.Path.Combine(fakeLoader, "mods", "TestMod"), SdkIOPath.GetModDirectory("TestMod"));
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "mods", "TestMod", "assets", "icon.png"),
                    SdkIOPath.GetModPath("TestMod", "assets", "icon.png"));

                Assert.Equal(System.IO.Path.Combine(fakeLoader, "mods", "TestMod", "data"), SdkIOPath.GetModDataDirectory("TestMod"));
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "mods", "TestMod", "data", "cards.json"),
                    SdkIOPath.GetModDataPath("TestMod", "cards.json"));

                Assert.Equal(System.IO.Path.Combine(fakeLoader, "mods", "MyMod", "config.json"),
                    SdkIOPath.GetModConfigPath("MyMod"));
                // 旧名 GetConfigPath 仍然可用，但已重定向到模组目录（带不带 .json 都行）。
#pragma warning disable CS0618
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "mods", "MyMod", "config.json"), SdkIOPath.GetConfigPath("MyMod"));
                Assert.Equal(System.IO.Path.Combine(fakeLoader, "mods", "MyMod", "config.json"), SdkIOPath.GetConfigPath("MyMod.json"));
#pragma warning restore CS0618

                Assert.Equal(System.IO.Path.Combine(fakeLoader, "skins", "summer", "card.png"),
                    SdkIOPath.GetSkinPath("summer", "card.png"));

                Assert.Equal(System.IO.Path.Combine(fakeLoader, "audio-schemes", "custom", "click.wav"),
                    SdkIOPath.GetAudioSchemePath("custom", "click.wav"));
            }
        }

        [Fact]
        public void GetModConfigPath_AlwaysUsesModDirectory()
        {
            string tempDir = TestEnv.NewTempDir("path-config-test");
            string modsDir = System.IO.Path.Combine(tempDir, "mods");
            string configsDir = System.IO.Path.Combine(tempDir, "configs");
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(configsDir);

            using (new EnvScope("CESIUM_LOADER_DIR", tempDir))
            using (new EnvScope("CESIUM_MODS_DIR", modsDir))
            {
                // 模组目录还不存在：路径照样拼接（调用方可能要写它）
                string modADir = System.IO.Path.Combine(modsDir, "ModA");
                Assert.Equal(System.IO.Path.Combine(modADir, "config.json"), SdkIOPath.GetModConfigPath("ModA"));

                // configs 下有同名文件也不再被采用（4.0.0 起集中目录已废弃）
                string configA = System.IO.Path.Combine(configsDir, "ModA.json");
                File.WriteAllText(configA, "{}");
                Assert.Equal(System.IO.Path.Combine(modADir, "config.json"), SdkIOPath.GetModConfigPath("ModA"));

                // 模组目录里那份是唯一结果
                string modBDir = System.IO.Path.Combine(modsDir, "ModB");
                Directory.CreateDirectory(modBDir);
                Assert.Equal(System.IO.Path.Combine(modBDir, "config.json"), SdkIOPath.GetModConfigPath("ModB"));
            }
        }

        [Fact]
        public void PathUtilities_WorkAsExpected()
        {
            Assert.Equal(@"foo\bar\baz", SdkIOPath.Combine("foo", "bar", "baz"));
            Assert.Equal("test.json", SdkIOPath.GetFileName(@"C:\foo\bar\test.json"));
            Assert.Equal("test", SdkIOPath.GetFileNameWithoutExtension(@"C:\foo\bar\test.json"));
            Assert.Equal(".json", SdkIOPath.GetExtension(@"C:\foo\bar\test.json"));
            Assert.Equal(@"C:\foo\bar\test.txt", SdkIOPath.ChangeExtension(@"C:\foo\bar\test.json", ".txt"));
            Assert.True(SdkIOPath.HasExtension(@"test.json"));
            Assert.False(SdkIOPath.HasExtension(@"test"));

            // Sanitize
            Assert.Equal("hello_world", SdkIOPath.SanitizeFileName("hello:world"));
            Assert.Equal("file_name", SdkIOPath.SanitizeFileName("file/name"));

            // Normalize
            Assert.Equal(@"C:\Games\AstralParty", SdkIOPath.Normalize("C:/Games/AstralParty/"));
        }

        [Fact]
        public void EnsureDirectoryExists_CreatesAndReturnsDirectory()
        {
            string temp = TestEnv.NewTempDir("ensure-dir");
            string target = System.IO.Path.Combine(temp, "sub1", "sub2");

            Assert.False(Directory.Exists(target));
            string result = SdkIOPath.EnsureDirectoryExists(target);
            Assert.Equal(target, result);
            Assert.True(Directory.Exists(target));
        }

        [Fact]
        public void PathsAndSdkPath_MatchPathBehavior()
        {
            string fakeGame = @"C:\Games\AstralParty";
            string fakeLoader = @"C:\Games\AstralParty\AstralParty_ModLoader";

            using (new EnvScope("CESIUM_GAME_DIR", fakeGame))
            using (new EnvScope("CESIUM_LOADER_DIR", fakeLoader))
            {
                // Verify Paths class
                Assert.Equal(SdkIOPath.LoaderDirectory, Paths.LoaderDirectory);
                Assert.Equal(SdkIOPath.GameDirectory, Paths.GameDirectory);
                Assert.Equal(SdkIOPath.ModsDirectory, Paths.ModsDirectory);
#pragma warning disable CS0618
                Assert.Equal(SdkIOPath.ConfigsDirectory, Paths.ConfigsDirectory);
                Assert.Equal(SdkIOPath.GetConfigPath("ModX"), Paths.GetConfigPath("ModX"));
#pragma warning restore CS0618
                Assert.Equal(SdkIOPath.GetModDirectory("ModX"), Paths.GetModDirectory("ModX"));
                Assert.Equal(SdkIOPath.GetModPath("ModX", "sub"), Paths.GetModPath("ModX", "sub"));

                // Verify SdkPath class
                Assert.Equal(SdkIOPath.LoaderDirectory, SdkPath.LoaderDirectory);
                Assert.Equal(SdkIOPath.GameDirectory, SdkPath.GameDirectory);
                Assert.Equal(SdkIOPath.ModsDirectory, SdkPath.ModsDirectory);
#pragma warning disable CS0618
                Assert.Equal(SdkIOPath.ConfigsDirectory, SdkPath.ConfigsDirectory);
#pragma warning restore CS0618
                Assert.Equal(SdkIOPath.GetModDirectory("ModX"), SdkPath.GetModDirectory("ModX"));
                Assert.Equal(SdkIOPath.GetModPath("ModX", "sub"), SdkPath.GetModPath("ModX", "sub"));
            }
        }
    }
}
