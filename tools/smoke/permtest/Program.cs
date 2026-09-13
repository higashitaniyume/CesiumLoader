// PermTest.cs - 权限模型单元测试(纯托管, 不依赖游戏)
// 验证: 敏感权限默认拒绝 / 声明后授予 / 覆盖配置生效 / 非敏感默认授予
//
// 用 Reflection.Emit 生成带"程序集级 [ModManifest]"声明的测试程序集
// (程序集级是权限声明的权威位置)。
using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using CesiumLoader.SDK;

namespace PermTest
{
    internal static class Program
    {
        private static int _fail;

        private static void Check(bool cond, string what)
        {
            Console.WriteLine((cond ? "  [PASS] " : "  [FAIL] ") + what);
            if (!cond) _fail++;
        }

        /// <summary>用 Reflection.Emit 生成一个带 [assembly: ModManifest] 声明的测试程序集。</summary>
        private static Assembly MakeTestAssembly(string name, ModPermission perms)
        {
            var an = new AssemblyName(name);
            var ab = AssemblyBuilder.DefineDynamicAssembly(an, AssemblyBuilderAccess.Run);
            var cb = ab.DefineDynamicModule("Main");
            var ctor = typeof(ModManifestAttribute).GetConstructor(new[] { typeof(string), typeof(string), typeof(string), typeof(string) });
            var permsProp = typeof(ModManifestAttribute).GetProperty("Permissions");
            var sdkProp = typeof(ModManifestAttribute).GetProperty("SdkVersion");
            var cab = new CustomAttributeBuilder(ctor,
                new object[] { name, "1.0.0", "test", "test mod" },
                new[] { permsProp, sdkProp },
                new object[] { perms, "2.0.0" });
            ab.SetCustomAttribute(cab);
            cb.DefineType("Dummy").CreateType();
            return ab;
        }

        private static int Main()
        {
            var noPerm = MakeTestAssembly("PermTestNoPerm", ModPermission.None);
            var speed = MakeTestAssembly("PermTestSpeed", ModPermission.SpeedHack);
            var action = MakeTestAssembly("PermTestAction", ModPermission.ReadGameState | ModPermission.GameActions);

            Console.WriteLine("=== 敏感权限默认关闭 ===");
            Check(!Permissions.Has(ModPermission.SpeedHack, noPerm), "无声明: SpeedHack 默认拒绝");
            Check(!Permissions.Has(ModPermission.GameActions, noPerm), "无声明: GameActions 默认拒绝");
            Check(Permissions.Has(ModPermission.ReadGameState, noPerm), "无声明: ReadGameState 默认授予(只读)");
            Check(Permissions.Has(ModPermission.FileWrite, noPerm), "无声明: FileWrite 默认授予");

            Console.WriteLine("=== 声明后授予 ===");
            Check(Permissions.Has(ModPermission.SpeedHack, speed), "声明 SpeedHack → 授予");
            Check(!Permissions.Has(ModPermission.GameActions, speed), "只声明 SpeedHack → GameActions 仍拒绝");
            Check(Permissions.Has(ModPermission.GameActions, action), "声明 GameActions → 授予");
            Check(Permissions.Has(ModPermission.ReadGameState, action), "声明 ReadGameState → 授予");

            Console.WriteLine("=== Require 门控 ===");
            Check(!Permissions.Require(ModPermission.SpeedHack, "SpeedHack.SetSpeed", noPerm), "Require: 无权限返回 false");
            Check(Permissions.Require(ModPermission.SpeedHack, "SpeedHack.SetSpeed", speed), "Require: 有权限返回 true");

            Console.WriteLine("=== 覆盖配置 (.permissions.json) ===");
            string tmp = Path.Combine(Path.GetTempPath(), "cesium_perm_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                // 一次性写两个覆盖文件(LoadOverrides 只加载一次)
                File.WriteAllText(Path.Combine(tmp, "override.permissions.json"),
                    "{\"PermTestNoPerm\": {\"ReadGameState\": false}}");
                File.WriteAllText(Path.Combine(tmp, "override2.permissions.json"),
                    "{\"PermTestNoPerm\": {\"SpeedHack\": true}}");
                Permissions.LoadOverrides(tmp);
                Check(!Permissions.Has(ModPermission.ReadGameState, noPerm), "覆盖配置: ReadGameState 被强制拒绝");
                Check(Permissions.Has(ModPermission.SpeedHack, noPerm), "覆盖配置: SpeedHack 被强制授予");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }

            Console.WriteLine("=== SDK 版本协商 ===");
            Check(SdkVersion.Accepts("2.0.0"), "mod 声明 2.0.0 ≤ 当前 2.0.0 → 兼容");
            Check(SdkVersion.Accepts("1.5.0"), "mod 声明 1.5.0 → 兼容");
            Check(!SdkVersion.Accepts("3.0.0"), "mod 声明 3.0.0 > 当前 2.0.0 → 拒绝");
            Check(SdkVersion.Accepts(null), "未声明 SDK 版本 → 兼容");

            Console.WriteLine();
            Console.WriteLine((_fail == 0 ? "全部通过" : $"{_fail} 项失败") + " (0 失败为通过)");
            return _fail == 0 ? 0 : 1;
        }
    }
}
