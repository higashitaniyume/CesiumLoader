// BootstrapHostTest - 在桌面 .NET 环境模拟原生代理的调用方式:
// 设置 CESIUM_* 环境变量, 直接调用 Bootstrap.Main(), 验证:
//   1. sdk/*.dll 被加载
//   2. mods/*.dll 被加载并调用 {Name}.ModEntry.Main()
//   3. 入口抛异常时被捕获记录, 不中断
//   4. 本次启动的 activity 日志被正确写入
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
        // 真实加载器每次启动都会把"这一次"的日志文件路径传下来(见 loader.cpp / logging.h),
        // 这里照做 —— 否则测到的只是"没有加载器时回退固定文件名"那条路。
        string session = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId;
        string activityLog = Path.Combine(logDir, "activity-mod-" + session + ".log");
        Environment.SetEnvironmentVariable("CESIUM_ACTIVITY_LOG_FILE", activityLog);
        Environment.SetEnvironmentVariable("CESIUM_ERROR_LOG_FILE", Path.Combine(logDir, "mod-errors-" + session + ".log"));

        Console.WriteLine("调用 Bootstrap.Main() ...");
        CesiumLoader.Bootstrap.Bootstrap.Main();
        Console.WriteLine("Bootstrap.Main() 返回(未崩溃)");

        string log = activityLog;
        string marker = Path.Combine(logDir, "testmod-ran.txt");

        Check(File.Exists(log), "本次启动的 activity 日志已生成");
        if (File.Exists(log))
        {
            string text = File.ReadAllText(log);
            Console.WriteLine("  [INFO] " + Path.GetFileName(log) + " 内容:");
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
