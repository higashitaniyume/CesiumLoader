using System;
using System.Collections.Generic;
using System.IO;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 诊断模块 + 运行时程序集反射服务。
    /// 诊断的价值在于"任何一项采集失败都不能让整份转储失败", 所以这里重点验证
    /// 离线(无 Unity / 无 IL2CPP)场景下每一层都能降级并产出可读文本。
    /// </summary>
    public class DiagnosticsTests
    {
        [Fact]
        public void RuntimeDump_IsFilledWithSdkFacts()
        {
            var dump = RuntimeDiagnostics.Collect();

            Assert.Equal(SdkVersion.Current, dump.SdkVersion);
            Assert.False(string.IsNullOrEmpty(dump.CapturedUtc));
            Assert.True(dump.AssemblyCount > 0, "至少能看到测试进程自己的程序集");
            Assert.True(dump.IsWindows, "测试运行在 Windows 上");
            Assert.Contains(dump.Assemblies, a => a == "CesiumLoader.SDK");
            Assert.True(dump.MainThreadExecuted >= 0);
            Assert.True(dump.UpdateSubscribers >= 0);
        }

        [Fact]
        public void RuntimeDump_Il2CppFieldsArePopulatedEvenWhenUnavailable()
        {
            var dump = RuntimeDiagnostics.Collect();

            // 测试进程没有加载 GameAssembly.dll → 必须报告"不可用", 且给出明细而不是空
            Assert.False(dump.Il2CppAvailable);
            Assert.False(dump.Il2CppAttached);
            Assert.False(string.IsNullOrEmpty(dump.Il2CppDetail));
            Assert.False(dump.RuntimeAssemblyLoaded);
        }

        [Fact]
        public void RuntimeDump_ToText_MentionsEverySection()
        {
            string text = RuntimeDiagnostics.Collect().ToText();

            Assert.Contains("CesiumLoader", text);
            Assert.Contains("IL2CPP", text);
            Assert.Contains("HybridCLR", text);
            Assert.Contains("主线程", text);
        }

        [Fact]
        public void RuntimeDump_ToJson_RoundTripsThroughCesiumJson()
        {
            string json = RuntimeDiagnostics.Collect().ToJson();

            object parsed;
            Assert.True(CesiumJson.TryDeserialize(json, out parsed));

            var map = parsed as Dictionary<string, object>;
            Assert.NotNull(map);
            Assert.Equal("2.1.5", map["SdkVersion"]);
            Assert.Equal(false, map["Il2CppAvailable"]);
        }

        [Fact]
        public void CameraDump_DegradesWhenNoRealUnityCameraExists()
        {
            // 用假后端: 诊断走的是强类型 Camera 路径, 因此应报"0 台相机", 而不是抛异常
            CameraService.SetBackend(new FakeCameraBackend());
            CameraService.InvalidateCaches();

            var dump = CameraDiagnostics.Collect();

            Assert.Equal(0, dump.CameraCount);
            Assert.False(dump.HasMainCamera);
            Assert.Empty(dump.Cameras);
            Assert.False(string.IsNullOrEmpty(dump.ToText()));
            Assert.False(string.IsNullOrEmpty(dump.ToJson()));

            CameraService.SetBackend(null);
        }

        [Fact]
        public void SceneDump_IsSafeOffline()
        {
            var dump = SceneDiagnostics.Collect();

            Assert.Equal(0, dump.SceneCount);
            Assert.True(string.IsNullOrEmpty(dump.ActiveSceneName));
            Assert.Equal(0, dump.RootObjectCount);
            Assert.Empty(dump.Scenes);
            Assert.True(dump.SceneSubscribers >= 0);
            Assert.Contains("场景", dump.ToText());
        }

        [Fact]
        public void SdkDiagnostics_SummaryAndDumpWriteToLogDirectory()
        {
            string path = SdkDiagnostics.GetDumpFilePath();
            Assert.EndsWith(SdkDiagnostics.DumpFileName, path);

            long before = SdkDiagnostics.DumpCount;
            string written = SdkDiagnostics.Dump("单元测试");

            Assert.Equal(before + 1, SdkDiagnostics.DumpCount);
            Assert.True(File.Exists(written), "诊断文件应被创建: " + written);

            string content = File.ReadAllText(written);
            Assert.Contains("单元测试", content);
            Assert.Contains("IL2CPP", content);
            Assert.Contains("SDK 计数器", content);

            Assert.False(string.IsNullOrEmpty(SdkDiagnostics.Summary()));
        }

        [Fact]
        public void SdkDiagnostics_JsonBundleIsValidJson()
        {
            string dir = TestEnv.NewTempDir("diag");
            string path = Path.Combine(dir, "bundle.json");

            string written = SdkDiagnostics.DumpJson(path);

            Assert.Equal(path, written);
            Assert.True(File.Exists(path));

            object parsed;
            Assert.True(CesiumJson.TryDeserialize(File.ReadAllText(path), out parsed));
        }

        [Fact]
        public void SdkDiagnostics_SingleSectionDumpsWriteFiles()
        {
            Assert.True(File.Exists(SdkDiagnostics.DumpCameraInfo()));
            Assert.True(File.Exists(SdkDiagnostics.DumpSceneInfo()));
            Assert.True(File.Exists(SdkDiagnostics.DumpRuntimeInfo()));
        }

        // =====================================================================
        // RuntimeAssemblyService(反射安全网)
        // =====================================================================

        [Fact]
        public void RuntimeAssemblyService_FindsItsOwnTypes()
        {
            Assert.True(RuntimeAssemblyService.IsAssemblyLoaded("CesiumLoader.SDK"));
            Assert.NotNull(RuntimeAssemblyService.FindAssembly("CesiumLoader.SDK"));
            Assert.True(RuntimeAssemblyService.HasType("CesiumLoader.SDK.ModBase"));
            Assert.NotNull(RuntimeAssemblyService.FindType("CesiumLoader.SDK.SdkVersion"));
            Assert.NotNull(RuntimeAssemblyService.FindType("CesiumLoader.SDK", "CesiumLoader.SDK.ModRegistry"));
            Assert.NotNull(RuntimeAssemblyService.FindTypeBySimpleName("ModRegistry"));
        }

        [Fact]
        public void RuntimeAssemblyService_MissingLookupsReturnNullNotThrow()
        {
            Assert.False(RuntimeAssemblyService.IsAssemblyLoaded("No.Such.Assembly"));
            Assert.Null(RuntimeAssemblyService.FindAssembly("No.Such.Assembly"));
            Assert.Null(RuntimeAssemblyService.FindType("No.Such.Type"));
            Assert.Null(RuntimeAssemblyService.FindType(null));
            Assert.Null(RuntimeAssemblyService.FindTypeBySimpleName("NoSuchTypeAnywhere"));
            Assert.Null(RuntimeAssemblyService.FindMethod(null, "Foo"));
            Assert.Null(RuntimeAssemblyService.FindField(null, "Foo"));
            Assert.Null(RuntimeAssemblyService.FindProperty(null, "Foo"));
        }

        [Fact]
        public void RuntimeAssemblyService_TypeCacheIsUsed()
        {
            long hitsBefore = RuntimeAssemblyService.TypeCacheHits;

            RuntimeAssemblyService.FindType("CesiumLoader.SDK.ModBase");
            RuntimeAssemblyService.FindType("CesiumLoader.SDK.ModBase");

            Assert.True(RuntimeAssemblyService.TypeLookupCount > 0);
            Assert.True(RuntimeAssemblyService.TypeCacheHits > hitsBefore);
        }

        [Fact]
        public void RuntimeAssemblyService_InvalidateCaches_KeepsWorking()
        {
            RuntimeAssemblyService.InvalidateCaches();

            Assert.NotNull(RuntimeAssemblyService.FindType("CesiumLoader.SDK.ModBase"));
        }

        [Fact]
        public void RuntimeAssemblyService_FindMethodAndFieldAndProperty()
        {
            var type = typeof(SdkVersion);

            Assert.NotNull(RuntimeAssemblyService.FindMethod(type, "Compare"));
            Assert.NotNull(RuntimeAssemblyService.FindMethod(type, "Accepts"));
            Assert.NotNull(RuntimeAssemblyService.FindField(type, "Current"));                     // Current 是 const 字段
            Assert.NotNull(RuntimeAssemblyService.FindProperty(typeof(ModConfig), "Count"));
        }

        [Fact]
        public void RuntimeAssemblyService_MemberAccessHelpersAreSafe()
        {
            // 反射访问失败必须是"返回默认值 + 记录日志", 而不是把异常抛给 mod
            Assert.Null(RuntimeAssemblyService.SafeInvokeStatic(null, "Foo"));
            Assert.Null(RuntimeAssemblyService.SafeInvokeStatic(typeof(SdkVersion), "NoSuchMethod"));
            Assert.Null(RuntimeAssemblyService.SafeInvoke(null, "Foo"));
            Assert.Null(RuntimeAssemblyService.SafeGetField(null, "Foo"));
            Assert.Null(RuntimeAssemblyService.SafeGetProperty(null, "Foo"));
            Assert.False(RuntimeAssemblyService.SafeSetField(null, "Foo", 1));
            Assert.False(RuntimeAssemblyService.SafeSetProperty(null, "Foo", 1));

            int fallback = RuntimeAssemblyService.SafeGetField(null, "Foo", 7);
            Assert.Equal(7, fallback);
        }

        [Fact]
        public void RuntimeAssemblyService_CanInvokeARealStaticMethod()
        {
            object result = RuntimeAssemblyService.SafeInvokeStatic(
                typeof(SdkVersion), "Compare", new object[] { "1.0.0", "1.2.0" }, "test");

            Assert.NotNull(result);
            Assert.True((int)result < 0);
        }

        [Fact]
        public void RuntimeAssemblyService_AssemblyNameListIsReadable()
        {
            var names = RuntimeAssemblyService.GetAssemblyNames();

            Assert.NotEmpty(names);
            Assert.Contains("CesiumLoader.SDK", names);
        }
    }
}
