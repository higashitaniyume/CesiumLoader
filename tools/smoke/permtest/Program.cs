// PermTest.cs - 权限声明元数据单元测试(纯托管, 不依赖游戏)
// 验证: 声明可读写, SDK 元数据可读取, permission API 保持无门控兼容行为
//
// 用 Reflection.Emit 生成带程序集级 [ModManifest] 声明的测试程序集。
using System;
using System.Reflection;
using System.Reflection.Emit;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Speed;

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
        private static Assembly MakeTestAssembly(string name)
        {
            var an = new AssemblyName(name);
            var ab = AssemblyBuilder.DefineDynamicAssembly(an, AssemblyBuilderAccess.Run);
            var cb = ab.DefineDynamicModule("Main");
            var ctor = typeof(ModManifestAttribute).GetConstructor(new[] { typeof(string), typeof(string), typeof(string), typeof(string) });
            var sdkProp = typeof(ModManifestAttribute).GetProperty("SdkVersion");
            var cab = new CustomAttributeBuilder(ctor,
                new object[] { name, "1.0.0", "test", "test mod" },
                new[] { sdkProp },
                new object[] { "2.0.0" });
            ab.SetCustomAttribute(cab);
            cb.DefineType("Dummy").CreateType();
            return ab;
        }

        private static int Main()
        {
            var noPerm = MakeTestAssembly("PermTestNoPerm");
            var speed = MakeTestAssembly("PermTestSpeed");
            var action = MakeTestAssembly("PermTestAction");

            Console.WriteLine("=== 声明与元数据读取 ===");
            Check(Permissions.Has(ModPermission.SpeedHack, noPerm), "权限门控已取消: SpeedHack 可用");
            Check(Permissions.Has(ModPermission.GameActions, noPerm), "权限门控已取消: GameActions 可用");
            Check(Permissions.Has(ModPermission.ReadGameState, noPerm), "权限门控已取消: ReadGameState 可用");
            Check(Permissions.Has(ModPermission.FileWrite, noPerm), "权限门控已取消: FileWrite 可用");
            Check(Permissions.Has(ModPermission.SpeedHack, speed), "声明 SpeedHack 后仍可用");
            Check(Permissions.Has(ModPermission.GameActions, action), "声明 GameActions 后仍可用");

            Console.WriteLine("=== Require 兼容 API ===");
            Check(Permissions.Require(ModPermission.SpeedHack, "SpeedHack.SetSpeed", noPerm), "Require 保持兼容且不再门控");
            Check(Permissions.Require(ModPermission.GameActions, "GameActions.ThrowDice", noPerm), "Require 对所有能力均不门控");

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
