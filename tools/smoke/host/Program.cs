// BootstrapHostTest - 在桌面 .NET 环境模拟原生代理的调用方式:
// 设置 CESIUM_* 环境变量, 直接调用 Bootstrap.Main(), 验证:
//   1. sdk/*.dll 被加载
//   2. mods/*.dll 被加载并调用 {Name}.ModEntry.Main()
//   3. 入口抛异常时被捕获记录, 不中断
//   4. activity-mod.log 被正确写入
using System;
using System.IO;

internal static class Program
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what);
        if (!ok) _failures++;
    }

    private static int Main(string[] args)
    {
        // 用绝对路径(基于本工具所在目录), 避免 dotnet run 工作目录差异
        // 参数: [envDir] [expectOk] [expectFail] —— 默认 env/1/1
        string env = args.Length > 0 ? args[0] : @"C:\src\Study\astralparty\modding\msvc\tools\smoke\env";
        int expectOk = args.Length > 1 ? int.Parse(args[1]) : 1;
        int expectFail = args.Length > 2 ? int.Parse(args[2]) : 1;        string sdkDir = Path.Combine(env, "sdk");
        string modsDir = Path.Combine(env, "mods");
        string logDir = Path.Combine(env, "logs");

        Environment.SetEnvironmentVariable("CESIUM_SDK_DIR", sdkDir);
        Environment.SetEnvironmentVariable("CESIUM_MODS_DIR", modsDir);
        Environment.SetEnvironmentVariable("CESIUM_LOG_DIR", logDir);

        Console.WriteLine("调用 Bootstrap.Main() ...");
        CesiumLoader.Bootstrap.Bootstrap.Main();
        Console.WriteLine("Bootstrap.Main() 返回(未崩溃)");

        string log = Path.Combine(logDir, "activity-mod.log");
        string marker = Path.Combine(logDir, "testmod-ran.txt");

        Check(File.Exists(log), "activity-mod.log 已生成");
        if (File.Exists(log))
        {
            string text = File.ReadAllText(log);
            Console.WriteLine("  [INFO] activity-mod.log 内容:");
            foreach (var line in text.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine("         " + line.TrimEnd('\r'));

            Check(text.Contains("SDK 加载成功: CesiumLoader.SDK.dll"), "SDK 加载日志");
            Check(text.Contains("Mod 加载成功: TestMod.dll"), "Mod 加载日志");
            Check(text.Contains($"Bootstrap 引导结束: {expectOk} 成功 / {expectFail} 失败"), $"汇总日志({expectOk} 成功 {expectFail} 失败)");
        }

        Check(File.Exists(marker), "mod 入口确实执行了(标记文件存在)");

        Console.WriteLine(_failures == 0 ? "全部通过" : _failures + " 项失败");
        return _failures == 0 ? 0 : 1;
    }
}
