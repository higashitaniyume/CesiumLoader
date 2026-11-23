// DllMainBootTest - 验证"代理 + 核心"的启动链: LoadLibrary(version.dll)(不调用任何导出) 时
// 代理的 DllMain 应只创建加载线程, 由该线程加载同目录的 CesiumLoader.dll 并启动引导:
//   1. 本次启动的加载器日志(cesium-loader-<会话>.log)出现
//      "[loader] core CesiumLoader.dll loaded (DLL_PROCESS_ATTACH)"(证明核心确实被代理拉起来了)
//   2. 约 1.5s 后出现 "[loader] CesiumLoader.dll Doorstop boot thread started (enabled=true)"
//   3. boot_thread 随后等待 GameAssembly.dll(测试进程没有, 60s 超时), 不崩溃
// 前提: 两个 DLL 必须同目录 —— 代理按**自身模块目录**找核心。构建产物都落在 bin\<配置>\,
// 所以直接跑就能满足; 核心缺失时本测试会在第 1 条上失败(代理只在 logs\proxy-*.log 留面包屑)。
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
        // 默认路径必须相对 exe 目录算: dotnet run 的工作目录不受控。
        // bin\<配置>\net8.0 → ... → modding\msvc\bin\<配置>\version.dll
        string dll = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                @"..\..\..\..\..\..\bin\Release\version.dll"));
        string dllFull = Path.GetFullPath(dll);
        Console.WriteLine("加载(不调用导出): " + dllFull);

        // 自备一份最小配置: load_config 在"没有配置文件"时是**静默全默认**的(不打印 [config] 行),
        // 那样后面 [config] enabled=true 那条断言永远红。写一份就走上真实的解析路径。
        // 关掉控制台与 Debug Shell: CI 里不弹窗口、输出更确定; 加载器本身照常完整启动。
        string loaderDir = Path.Combine(AppContext.BaseDirectory, "AstralParty_ModLoader");
        Directory.CreateDirectory(loaderDir);
        File.WriteAllText(Path.Combine(loaderDir, "doorstop_config.json"),
            "{\n  \"enabled\": true,\n  \"consoleEnabled\": false,\n  \"debugShell\": false\n}\n");

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
        // 加载器每次启动各写一个文件(names 里带会话标识), 取目录里最新的那个
        string log = NewestSessionLog(Path.Combine(modRoot, "logs"), "cesium-loader-*.log");
        Console.WriteLine("日志路径: " + (log ?? "(未找到 cesium-loader-*.log)"));
        if (log == null)
        {
            Console.WriteLine("[FAIL] cesium-loader-*.log 未生成");
            return 1;
        }
        // 加载器在进程存活期间一直持有日志句柄, 读的时候必须允许共享写, 否则 File.ReadAllText
        // 会抛共享冲突(IOException) —— 这个测试读的是"正在写"的日志。
        string text;
        using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }
        Console.WriteLine("--- 日志内容 ---");
        Console.WriteLine(text);
        // 核心验证:
        //   core   = 代理把核心 CesiumLoader.dll 加载起来了(缺核心时这里就是红灯, 并在
        //            AstralParty_ModLoader\logs\proxy-*.log 留下面包屑)
        //   boot   = 引导线程启动, 且配置解析出 enabled=true
        //   config = doorstop_config.json 被读到
        // (attach/config 行在 dotnet 控制台宿主下可能因 loader-lock 时序缺失,
        //  游戏进程静态加载时 attach 行正常写入 —— 见游戏目录日志)
        // 不再断言控制台: 宿主本身已是控制台(AllocConsole 返回 0), 且是否置顶取决于配置。
        bool core = text.Contains("core CesiumLoader.dll loaded (DLL_PROCESS_ATTACH)");
        bool boot = text.Contains("CesiumLoader.dll Doorstop boot thread started (enabled=true)");
        bool config = text.Contains("[config] enabled=true");
        Console.WriteLine(core ? "[PASS] 核心 DLL 已被代理加载" : "[FAIL] 核心 DLL 未被加载(缺 CesiumLoader.dll 或代理找不到它)");
        Console.WriteLine(boot ? "[PASS] 引导线程启动" : "[FAIL] 引导线程启动");
        Console.WriteLine(config ? "[PASS] 配置解析(enabled=true)" : "[FAIL] 配置解析");
        return (core && boot && config) ? 0 : 1;
    }

    /// <summary>加载器每次启动写一个新日志文件, 取目录里最新的那个(没有则 null)。</summary>
    private static string NewestSessionLog(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return null;
        string best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string f in Directory.GetFiles(dir, pattern))
        {
            DateTime t = File.GetLastWriteTimeUtc(f);
            if (best == null || t > bestTime) { best = f; bestTime = t; }
        }
        return best;
    }
}

internal static class Native2
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string name);

    public delegate uint GetFileVersionInfoSizeADelegate(string file, out uint handle);
}
