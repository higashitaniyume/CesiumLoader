// SpeedHackTest - 验证 version.dll 的变速引擎:
//   1. LoadLibrary 我们的 version.dll (DllMain 会安装 hook)
//   2. 验证 ap_speed_active = true
//   3. 测量 GetTickCount64 真实流逝 vs 倍率缩放后的流逝
//   4. 设置 2x / 0.5x 倍率, 确认虚拟时间按倍率走
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileName(IntPtr hModule, System.Text.StringBuilder buf, uint size);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [DllImport("winmm.dll")]
    private static extern uint timeGetTime();

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    // 加载器的 ap_speed_* 导出
    [DllImport("version.dll", EntryPoint = "ap_speed_active")]
    private static extern bool ap_speed_active();

    [DllImport("version.dll", EntryPoint = "ap_speed_get")]
    private static extern double ap_speed_get();

    [DllImport("version.dll", EntryPoint = "ap_speed_set")]
    private static extern bool ap_speed_set(double speed);

    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what);
        if (!ok) _failures++;
    }

    private static int Main(string[] args)
    {
        string dll = args.Length > 0 ? args[0] : @"..\..\..\..\..\src\CesiumLoader\bin\Release\version.dll";
        Console.WriteLine("加载: " + System.IO.Path.GetFullPath(dll));
        IntPtr h = LoadLibrary(System.IO.Path.GetFullPath(dll));
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"[FAIL] LoadLibrary 错误码 {err} (0x{err:X8})");
            // 常见: dotnet host 已加载系统 version.dll → 模块名去重, 我们的不会进
            // 验证: 检查加载到的模块路径
            var buf = new System.Text.StringBuilder(1024);
            GetModuleFileName(h, buf, 1024);
            Console.WriteLine("当前模块: " + buf);
            return 1;
        }
        Console.WriteLine("LoadLibrary OK");

        Thread.Sleep(2500); // 等 boot_thread Sleep(1500) + speedhack_init() 完成

        Console.WriteLine("ap_speed_active: " + ap_speed_active());
        Check(ap_speed_active(), "变速引擎已激活");

        // --- 倍率 1.0 基线: 虚拟流逝 ≈ 真实流逝 ---
        double measureReal = MeasureRealMs(500);
        double measureVirtual = MeasureVirtualMs(500);
        Console.WriteLine($"  真实 500ms -> 真实测量 {measureReal:F0}ms");
        Check(Math.Abs(measureVirtual - measureReal) < 150,
            $"1.0x 虚拟时间≈真实时间 (虚拟 {measureVirtual:F0}ms)");

        // --- 2x: 虚拟流逝 ≈ 2 * 真实 ---
        Check(ap_speed_set(2.0), "设置 2x 成功");
        Thread.Sleep(50); // 等状态更新
        double v2 = MeasureVirtualMs(400);
        Console.WriteLine($"  2.0x: 虚拟流逝 {v2:F0}ms");
        Check(v2 > 500 && v2 < 1200, $"2.0x 虚拟时间≈2x真实 (虚拟 {v2:F0}ms, 期望≈800)");

        // --- 0.5x: 虚拟流逝 ≈ 0.5 * 真实 ---
        Check(ap_speed_set(0.5), "设置 0.5x 成功");
        Thread.Sleep(50);
        double v05 = MeasureVirtualMs(400);
        Console.WriteLine($"  0.5x: 虚拟流逝 {v05:F0}ms");
        Check(v05 > 100 && v05 < 350, $"0.5x 虚拟时间≈0.5x真实 (虚拟 {v05:F0}ms, 期望≈200)");

        // --- 恢复 ---
        Check(ap_speed_set(1.0), "恢复 1x 成功");
        double v1 = MeasureVirtualMs(300);
        Console.WriteLine($"  1.0x: 虚拟流逝 {v1:F0}ms");
        Check(Math.Abs(v1 - 300) < 150, $"恢复后虚拟时间≈真实 (虚拟 {v1:F0}ms)");

        Console.WriteLine(_failures == 0 ? "全部通过" : $"{_failures} 项失败");
        return _failures == 0 ? 0 : 1;
    }

    // 真实流逝(不受 hook 影响的绝对计时: 用 Stopwatch? 不行, Stopwatch 用 QPC 会被 hook!
    // 用 Process.GetCurrentProcess().TotalProcessorTime? 太粗。用 DateTime.UtcNow (墙钟) 作真实基准)
    private static double MeasureRealMs(int sleepMs)
    {
        var sw = new Stopwatch(); // Stopwatch 内部用 QPC, 会被 hook! 不能用
        var t0 = DateTime.UtcNow;
        Thread.Sleep(sleepMs);
        return (DateTime.UtcNow - t0).TotalMilliseconds;
    }

    // 虚拟流逝(游戏感知的时间)
    private static double MeasureVirtualMs(int sleepMs)
    {
        ulong t0 = GetTickCount64();
        Thread.Sleep(sleepMs);
        return (double)(GetTickCount64() - t0);
    }
}
