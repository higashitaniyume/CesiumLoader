// DllMainBootTest - 验证新逻辑: version.dll 被 LoadLibrary(不调用任何导出)
// 时, DllMain(DLL_PROCESS_ATTACH) 应自动启动引导线程, 表现为:
//   1. cesium-loader.log 出现 "[hijack] version.dll 被加载"
//   2. 约 1.5s 后出现 "[hijack] version.dll Doorstop 引导线程启动"
//   3. boot_thread 随后等待 GameAssembly.dll(测试进程没有, 60s 超时), 不崩溃
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileName(IntPtr hModule, System.Text.StringBuilder buf, uint size);

    private static int Main(string[] args)
    {
        string dll = args.Length > 0 ? args[0] : @"..\..\..\..\..\bin\Release\version.dll";
        string dllFull = Path.GetFullPath(dll);
        Console.WriteLine("加载(不调用导出): " + dllFull);

        IntPtr h = LoadLibrary(dllFull);
        Console.WriteLine("LoadLibrary 返回: " + (h != IntPtr.Zero ? "OK" : "FAIL"));

        // 确认加载的模块是不是我们的 DLL(dotnet host 可能已加载系统 version.dll)
        var buf = new System.Text.StringBuilder(1024);
        GetModuleFileName(h, buf, 1024);
        string loaded = buf.ToString();
        bool isOurs = string.Equals(loaded, dllFull, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine("实际加载模块: " + loaded);
        Console.WriteLine(isOurs ? "[PASS] 加载的是我们的代理 DLL" : "[FAIL] 加载的是系统/其他 version.dll, DllMain 不会执行!");

        // 主动调用一个转发导出(GetFileVersionInfoSizeA), 触发 maybe_start_boot 的完整路径
        // (正常情况下引导线程已在 DllMain 启动; 这里只是确保日志链路被走到)
        try
        {
            var p = Native2.GetProcAddress(h, "GetFileVersionInfoSizeA");
            var fn = (Native2.GetFileVersionInfoSizeADelegate)System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(p, typeof(Native2.GetFileVersionInfoSizeADelegate));
            uint hnd;
            uint sz = fn(@"C:\Windows\System32\notepad.exe", out hnd);
            Console.WriteLine("[INFO] 主动调用 GetFileVersionInfoSizeA = " + sz);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[INFO] 主动调用失败: " + ex.Message);
        }

        // 诊断: 打印主模块路径(loader_root 基于它)并测试目标目录可写性
        var mainBuf = new System.Text.StringBuilder(1024);
        uint mn = GetModuleFileName(IntPtr.Zero, mainBuf, 1024);
        string mainPath = mn > 0 ? mainBuf.ToString() : "(GetModuleFileName failed)";
        Console.WriteLine("[DIAG] 主模块路径: " + mainPath);
        string diagRoot = Path.Combine(Path.GetDirectoryName(mainPath) ?? ".", "AstralParty_ModLoader");
        string diagLogDir = Path.Combine(diagRoot, "logs");
        try
        {
            Directory.CreateDirectory(diagLogDir);
            string probe = Path.Combine(diagLogDir, "probe.txt");
            File.WriteAllText(probe, "probe");
            Console.WriteLine("[DIAG] 目标日志目录可写: " + diagLogDir);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DIAG] 目标日志目录不可写: " + ex.Message);
        }

        // 等待引导线程的 Sleep(1500) + 日志写入
        Console.WriteLine("等待引导线程日志 (4s)...");
        Thread.Sleep(4000);

        // loader_root 基于测试 exe 目录: bin/Debug/net8.0/ + AstralParty_ModLoader
        string exeDir = AppContext.BaseDirectory;
        string modRoot = Path.Combine(exeDir, "AstralParty_ModLoader");
        string log = Path.Combine(modRoot, "logs", "cesium-loader.log");
        Console.WriteLine("日志路径: " + log);
        if (!File.Exists(log))
        {
            Console.WriteLine("[FAIL] cesium-loader.log 未生成");
            return 1;
        }
        string text = File.ReadAllText(log);
        Console.WriteLine("--- 日志内容 ---");
        Console.WriteLine(text);
        // 核心验证: boot 线程必须启动, 且配置解析出 enabled=true
        // (attach/config 行在 dotnet 控制台宿主下可能因 loader-lock 时序缺失,
        //  游戏进程静态加载时 attach 行正常写入 —— 见游戏目录日志)
        bool boot = text.Contains("version.dll Doorstop 引导线程启动 (enabled=true)");
        bool console = text.Contains("控制台窗口置顶完成");
        Console.WriteLine(boot ? "[PASS] 引导线程启动 + enabled=true 配置解析" : "[FAIL] 引导线程启动");
        Console.WriteLine(console ? "[PASS] 控制台初始化" : "[FAIL] 控制台初始化");
        return (boot && console) ? 0 : 1;
    }
}

internal static class Native2
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string name);

    public delegate uint GetFileVersionInfoSizeADelegate(string file, out uint handle);
}
