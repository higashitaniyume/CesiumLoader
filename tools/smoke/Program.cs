// VersionSmokeTest - 冒烟测试: 验证 CesiumLoader 编译出的 version.dll
// (Doorstop 代理) 能正确转发到系统 C:\Windows\System32\version.dll。
//
// 做法: 用绝对路径 LoadLibrary 加载我们的 version.dll, 确认加载的是我们的
// 模块(而不是系统已加载的同名模块), 然后通过 GetProcAddress 调用
// GetFileVersionInfoSizeA / GetFileVersionInfoA / VerQueryValueA
// (正是 UnityPlayer.dll 导入的那 3 个函数), 读取真实文件的版本信息,
// 并与直接调用系统 version.dll 的结果对比。

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetModuleFileName(IntPtr hModule, StringBuilder buf, uint size);

    // 直接链接到系统 version.dll 的引用(对比组)
    [DllImport("version.dll", CharSet = CharSet.Ansi)]
    public static extern uint GetFileVersionInfoSizeA(string file, out uint handle);

    [DllImport("version.dll", CharSet = CharSet.Ansi)]
    public static extern bool GetFileVersionInfoA(string file, uint handle, uint len, byte[] data);

    [DllImport("version.dll", CharSet = CharSet.Ansi)]
    public static extern bool VerQueryValueA(byte[] block, string subBlock, out IntPtr buffer, out uint len);
}

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
        // 默认路径相对 exe 目录算(dotnet run 的工作目录不受控):
        // bin\<配置>\net8.0 → ... → modding\msvc\bin\<配置>\version.dll(代理)。
        // 核心 CesiumLoader.dll 与它同目录 —— 代理按自身模块目录去找核心。
        string ourDll = Path.GetFullPath(args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\bin\Release\version.dll"));
        string targetFile = Path.GetFullPath(args.Length > 1 ? args[1] : Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\notepad.exe");

        Console.WriteLine("测试对象: " + ourDll);
        Console.WriteLine("版本读取目标: " + targetFile);
        if (!File.Exists(ourDll)) { Console.WriteLine("[FAIL] version.dll 不存在"); return 1; }
        if (!File.Exists(targetFile)) { Console.WriteLine("[FAIL] 目标文件不存在"); return 1; }

        // 1. 全路径加载我们的 version.dll
        IntPtr h = Native.LoadLibrary(ourDll);
        Check(h != IntPtr.Zero, "LoadLibrary(" + ourDll + ")");
        if (h == IntPtr.Zero) return 1;

        // 2. 确认加载的模块确实是我们的(而不是系统已加载的同名 version.dll)
        var pathBuf = new StringBuilder(1024);
        Native.GetModuleFileName(h, pathBuf, 1024);
        string loadedPath = pathBuf.ToString();
        bool isOurs = string.Equals(loadedPath, ourDll, StringComparison.OrdinalIgnoreCase);
        Check(isOurs, "加载的模块是代理 DLL (实际: " + loadedPath + ")");
        if (!isOurs)
        {
            Console.WriteLine("  [INFO] 进程内已存在系统 version.dll, 无法测试代理转发。");
            return 2;
        }

        // 3. GetProcAddress 取 3 个关键导出
        IntPtr pSize = Native.GetProcAddress(h, "GetFileVersionInfoSizeA");
        IntPtr pInfo = Native.GetProcAddress(h, "GetFileVersionInfoA");
        IntPtr pQuery = Native.GetProcAddress(h, "VerQueryValueA");
        Check(pSize != IntPtr.Zero, "导出 GetFileVersionInfoSizeA");
        Check(pInfo != IntPtr.Zero, "导出 GetFileVersionInfoA");
        Check(pQuery != IntPtr.Zero, "导出 VerQueryValueA");

        // 3b. 代理必须导出完整集合。漏一个导出是**静默**失败: 调用方 GetProcAddress 拿到 null,
        //     或者按导入表解析的模块直接加载失败。而 .def 恰恰有个坑 —— 注释行里出现非 ASCII
        //     字节(比如中文)会把紧随其后的那条导出名吃掉(link 14.51 实测: ap_speed_set 消失,
        //     无警告无报错)。所以这里把 20 个名字逐个点一遍, 让"漏导出"在冒烟阶段就红掉,
        //     而不是等游戏里某个调用方悄悄失效。version.def 现在也强制纯 ASCII。
        string[] expected =
        {
            "GetFileVersionInfoA", "GetFileVersionInfoByHandle", "GetFileVersionInfoExA", "GetFileVersionInfoExW",
            "GetFileVersionInfoSizeA", "GetFileVersionInfoSizeExA", "GetFileVersionInfoSizeExW",
            "GetFileVersionInfoSizeW", "GetFileVersionInfoW",
            "VerFindFileA", "VerFindFileW", "VerInstallFileA", "VerInstallFileW", "VerQueryValueA", "VerQueryValueW",
            "ap_speed_set", "ap_speed_get", "ap_speed_active", "ap_console_write", "ap_console_write_w",
        };
        var missing = new System.Collections.Generic.List<string>();
        foreach (string name in expected)
        {
            if (Native.GetProcAddress(h, name) == IntPtr.Zero) missing.Add(name);
        }
        Check(missing.Count == 0,
            "代理导出完整 (" + expected.Length + " 个)" +
            (missing.Count > 0 ? "，缺少: " + string.Join(", ", missing) : ""));

        // 4. 通过代理调用(与系统对比)
        uint sysSize = Native.GetFileVersionInfoSizeA(targetFile, out uint sysHandle);
        Console.WriteLine("  [INFO] 系统 GetFileVersionInfoSizeA = " + sysSize);

        uint size = GetViaProxy(pSize, targetFile, out uint handle);
        Check(size == sysSize, "代理 GetFileVersionInfoSizeA = " + size + " (系统 " + sysSize + ")");

        byte[] block = new byte[Math.Max(size, sysSize)];
        bool sysOk = Native.GetFileVersionInfoA(targetFile, sysHandle, sysSize, block);
        bool proxyOk = GetInfoViaProxy(pInfo, targetFile, handle, size, block);
        Check(proxyOk == sysOk, "代理 GetFileVersionInfoA = " + proxyOk + " (系统 " + sysOk + ")");

        // VerQueryValueA: 读 FileVersion
        bool sysQ = Native.VerQueryValueA(block, "\\", out IntPtr sysBuf, out uint sysLen);
        bool proxyQ = QueryViaProxy(pQuery, block, "\\", out IntPtr proxyBuf, out uint proxyLen);
        Check(proxyQ == sysQ, "代理 VerQueryValueA = " + proxyQ + " (系统 " + sysQ + ")");
        if (proxyQ && sysQ)
        {
            string sysVer = Marshal.PtrToStringAnsi(sysBuf);
            string proxyVer = Marshal.PtrToStringAnsi(proxyBuf);
            Check(sysVer == proxyVer, "FileVersion 一致: \"" + proxyVer + "\"");
        }

        Console.WriteLine(_failures == 0 ? "全部通过" : _failures + " 项失败");
        return _failures == 0 ? 0 : 1;
    }

    // ---- 通过代理 DLL 的函数指针调用 ----

    private delegate uint GetFileVersionInfoSizeADelegate(string file, out uint handle);
    private delegate bool GetFileVersionInfoADelegate(string file, uint handle, uint len, byte[] data);
    private delegate bool VerQueryValueADelegate(byte[] block, string sub, out IntPtr buffer, out uint len);

    private static uint GetViaProxy(IntPtr fn, string file, out uint handle)
    {
        var d = (GetFileVersionInfoSizeADelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(GetFileVersionInfoSizeADelegate));
        return d(file, out handle);
    }

    private static bool GetInfoViaProxy(IntPtr fn, string file, uint handle, uint len, byte[] data)
    {
        var d = (GetFileVersionInfoADelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(GetFileVersionInfoADelegate));
        return d(file, handle, len, data);
    }

    private static bool QueryViaProxy(IntPtr fn, byte[] block, string sub, out IntPtr buffer, out uint len)
    {
        var d = (VerQueryValueADelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(VerQueryValueADelegate));
        return d(block, sub, out buffer, out len);
    }
}
