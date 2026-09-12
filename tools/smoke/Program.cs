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
        string ourDll = Path.GetFullPath(args.Length > 0 ? args[0] : @"..\..\..\..\bin\Release\version.dll");
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
