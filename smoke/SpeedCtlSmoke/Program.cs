// 变速控制文件通道的冒烟测试(不依赖游戏)
//
// 为什么需要它: 热更程序集无法 P/Invoke, mod 与加载器只能通过文件通信 ——
// 这条通道在游戏里不好单独验证, 一旦坏了表现是"按热键没反应"(很难查)。
// 本宿主把加载器 version.dll 放在自己旁边, 用一次转发导出触发引导线程, 然后
// 像 mod 一样读写控制文件, 校验:
//   - state.txt 的字段与初始值(基础倍率/hook 数);
//   - 合法请求会被应用(~100ms 内回写);
//   - 非法请求(nan/越界/多小数点/空)一律忽略且不改变倍率。
//
// 运行: pwsh -NoProfile -File tools\smoke-speedctl.ps1
// 原理: loader_root() = 宿主 exe 目录\AstralParty_ModLoader, 所以这里能造出
//       与游戏一致的目录布局; GameAssembly.dll 等待会超时(配置成 1 秒),
//       但通道在等待之前就已就绪 —— 正是要测的部分。

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    // version.dll 的转发导出(调用它会触发加载器引导线程), 这里只用它当"启动开关"。
    [DllImport("version.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern uint GetFileVersionInfoSizeA(string name, out uint handle);

    // 被加载器 hook 的时钟 —— 读到的就是"游戏感知的时间"。
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ulong GetTickCount64();

    /// <summary>虚拟时钟(= 游戏看到的时间)。加载器在本进程里装了 hook, 所以这里会经过 hook。</summary>
    private static ulong VirtualTick() => GetTickCount64();

    /// <summary>
    /// 真实经过的毫秒数。**必须用 DateTime.UtcNow**: Stopwatch(QPC) 与
    /// Environment.TickCount(64) 都在 hook 名单里, 用它们计时会被倍率缩放
    /// (4x 时"3 秒超时"实际只有 0.75 秒, 测试会莫名失败)。
    /// </summary>
    private static long RealMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

    private static string _root, _loader, _state, _request, _log;
    private static int _failures;

    private static int Main()
    {
        _root = Path.GetDirectoryName(Environment.ProcessPath);
        _loader = Path.Combine(_root, "AstralParty_ModLoader");
        _state = Path.Combine(_loader, "speed", "state.txt");
        _request = Path.Combine(_loader, "speed", "request.txt");
        _log = Path.Combine(_loader, "logs", "cesium-loader.log");

        Console.WriteLine("[smoke] 宿主: " + _root);
        Console.WriteLine("[smoke] 触发加载器引导...");
        try { GetFileVersionInfoSizeA("nonexistent.exe", out _); }
        catch (Exception e) { Console.WriteLine("[smoke] 导出调用异常(可忽略): " + e.Message); }

        for (int i = 0; i < 60 && !File.Exists(_state); i++) Thread.Sleep(100);
        if (!File.Exists(_state))
        {
            Console.WriteLine("[smoke] ✗ state.txt 未出现: 控制文件通道没起来");
            DumpLog();
            return 1;
        }

        Console.WriteLine("[smoke] ✓ state.txt 已就绪:");
        foreach (var line in File.ReadAllLines(_state)) Console.WriteLine("        " + line.TrimEnd());

        // 初始值: base=2.0(speedhackBaseSpeed=2), hooks=4, speed 跟随基础倍率
        Eq("初始 speed 跟随基础倍率", Read("speed"), "2.000");
        Eq("初始 base", Read("base"), "2.000");
        Eq("初始 hooks=4", Read("hooks"), "4");
        Eq("初始 active=1", Read("active"), "1");
        True("初始 version 非空", (Read("version") ?? "").Length > 0);

        // 合法请求: 调整倍率
        Apply("2.500");
        Apply("1.000");   // 1.0 = "恢复正常倍率" —— 引擎必须保持挂着 hook(不卸载)
        Eq("1.0x 时 hook 仍全部在挂(hooks=4)", Read("hooks"), "4");
        Eq("1.0x 时引擎仍处于激活状态(active=1)", Read("active"), "1");
        Apply("7.250");
        Apply("1.000");   // 1.0 是硬下限(合法)

        // 硬规则: 低于 1 倍的请求必须在加载器层被拦掉 —— 引擎里绝不能出现 <1 的倍率。
        // 这里连"贴着下限"的 0.999 也要拒绝, 避免"四舍五入一下就能减速"。
        foreach (var low in new[] { "0.999", "0.500", "0.100", "0.0001" })
        {
            WriteRequest(low);
            Thread.Sleep(250);
            Eq("减速请求 '" + low + "' 被拒绝(引擎保持 1.000)", Read("speed"), "1.000");
        }

        // 非法请求: 一律忽略, 倍率保持在 1.000
        foreach (var bad in new[] { "hello", "nan", "inf", "-1", "0", "1000", "2.5.5", "", "  ", "0x2" })
        {
            WriteRequest(bad);
            Thread.Sleep(250);
            Eq("非法请求 '" + bad + "' 被忽略", Read("speed"), "1.000");
        }

        // 请求文件内容必须原样保留(加载器只读它, 不改写)
        Eq("请求文件不被加载器改写", File.ReadAllText(_request).Trim(), "0x2");

        // 时间连续性: 切换倍率(尤其是切回 1.0x)时虚拟时钟不能跳变
        ClockContinuity();

        // LocalAppData 镜像: mod 侧定位不到游戏目录时, 这是最后一档候选
        MirrorCheck();

        // ===== SDK 端到端: 反射加载真实 CesiumLoader.SDK.dll, 走 mod 实际会走的代码路径 =====
        // 这一段覆盖线上踩过的坑: "mod 读不到 state.txt" —— 既验证正常路径, 也验证
        // 环境变量全部读不到时能否靠路径推导自愈。
        SdkEndToEnd(_loader);

        Console.WriteLine();
        DumpLog();
        Console.WriteLine(_failures == 0 ? "[smoke] === 全部通过 ===" : $"[smoke] === {_failures} 项失败 ===");
        return _failures == 0 ? 0 : 2;
    }

    /// <summary>镜像目录(%LocalAppData%\AstralParty_ModLoader\speed)必须能独立工作。</summary>
    private static void MirrorCheck()
    {
        Console.WriteLine();
        Console.WriteLine("[smoke] --- LocalAppData 镜像目录 ---");

        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(local))
        {
            Console.WriteLine("  ! 跳过: 没有 LOCALAPPDATA");
            return;
        }

        string mirror = Path.Combine(local, "AstralParty_ModLoader", "speed");
        string state = Path.Combine(mirror, "state.txt");
        string request = Path.Combine(mirror, "request.txt");
        Console.WriteLine("  镜像目录: " + mirror);

        True("镜像目录里有 state.txt(加载器同时写两份)", File.Exists(state));
        if (!File.Exists(state)) return;   // 没镜像就到此为止, 别让写请求把测试搞崩
        Console.WriteLine("  镜像 state: " + File.ReadAllText(state).Replace("\r\n", " ").Trim());

        // 从镜像目录发请求, 加载器同样要认
        WriteRequestTo(request, "1.750");
        long mirrorStart = RealMs();
        while (RealMs() - mirrorStart < 3000 && Read("speed") != "1.750") Thread.Sleep(25);
        Eq("写进镜像目录的请求被加载器应用", Read("speed"), "1.750");
    }

    private static void SdkEndToEnd(string loaderDir)
    {
        Console.WriteLine();
        Console.WriteLine("[smoke] --- SDK 端到端(反射加载真实 SDK) ---");

        string sdkPath = Path.Combine(loaderDir, "sdk", "CesiumLoader.SDK.dll");
        if (!File.Exists(sdkPath))
        {
            Console.WriteLine("  ! 跳过: 没有 " + sdkPath);
            return;
        }

        Type type;
        try
        {
            type = System.Reflection.Assembly.LoadFrom(sdkPath).GetType("CesiumLoader.SDK.SpeedHack", true);
        }
        catch (Exception e)
        {
            Fail("加载 SDK 失败: " + e.GetType().Name + " " + e.Message);
            return;
        }

        Func<string, object> get = name => type.GetProperty(name).GetValue(null, null);

        try
        {
            // 1) 正常路径: 加载器在宿主进程里设好了环境变量, SDK 应当直接可用
            bool available = (bool)get("IsAvailable");
            string dir = (string)get("Directory");
            string envSpeed = Environment.GetEnvironmentVariable("CESIUM_SPEED_DIR");
            Console.WriteLine("  CESIUM_SPEED_DIR = " + (envSpeed ?? "(null)"));
            Console.WriteLine("  SDK 目录 = " + (dir ?? "(null)"));
            Console.WriteLine("  " + (string)type.GetMethod("Diagnostics").Invoke(null, null));

            True("SDK 认为引擎可用", available);
            True("SDK 定位到的目录里真的有 state.txt",
                 !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "state.txt")));

            // 加载器确实注入了环境变量时, SDK 必须优先用它(游戏里的主路径)
            if (!string.IsNullOrEmpty(envSpeed))
                True("SDK 优先使用加载器注入的 CESIUM_SPEED_DIR",
                     dir != null && dir.Equals(envSpeed, StringComparison.OrdinalIgnoreCase));
            else
                Console.WriteLine("  (宿主里没有 CESIUM_SPEED_DIR —— 加载器未走到设环境变量那一步, 跳过该断言)");

            // 2) 通过 SDK 请求倍率 -> 加载器应用 -> SDK 回读(完整往返)
            var setSpeed = type.GetMethod("SetSpeed");
            bool wrote = (bool)setSpeed.Invoke(null, new object[] { 3.250 });
            True("SDK SetSpeed(3.250) 返回 true", wrote);

            long sdkStart = RealMs();
            while (RealMs() - sdkStart < 3000 &&
                   Math.Abs((double)get("Speed") - 3.250) > 1e-9) Thread.Sleep(25);
            True("加载器在 3 秒内应用了 SDK 的请求(回读 3.250)", Math.Abs((double)get("Speed") - 3.250) < 1e-9);

            // 3) 关键回归: 环境变量全部读不到(模拟线上那种"找不到状态文件") -> 必须靠路径推导自愈
            string savedSpeed = Environment.GetEnvironmentVariable("CESIUM_SPEED_DIR");
            string savedLog = Environment.GetEnvironmentVariable("CESIUM_LOG_DIR");
            Environment.SetEnvironmentVariable("CESIUM_SPEED_DIR", null);
            Environment.SetEnvironmentVariable("CESIUM_LOG_DIR", null);
            try
            {
                type.GetMethod("Refresh").Invoke(null, null);
                bool stillAvailable = (bool)get("IsAvailable");
                string stillDir = (string)get("Directory");
                Console.WriteLine("  无环境变量时: 可用=" + stillAvailable + " 目录=" + (stillDir ?? "(null)"));
                Console.WriteLine("  " + (string)type.GetMethod("Diagnostics").Invoke(null, null));
                True("环境变量不可读时仍能定位控制目录", stillAvailable);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CESIUM_SPEED_DIR", savedSpeed);
                Environment.SetEnvironmentVariable("CESIUM_LOG_DIR", savedLog);
                type.GetMethod("Refresh").Invoke(null, null);
            }
        }
        catch (Exception e)
        {
            Fail("SDK 端到端异常: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>
    /// 变速的"时间连续性"回归测试 —— 专治实测踩到的坑:
    ///
    /// 若 1.0 倍的 hook 图省事"直接返回真实时间", 就绕过了 offset, 而切速时 offset 恰恰是
    /// "当时的虚拟时间"。于是从 2x(或 4x)切回 1.0x 的瞬间, 游戏看到的时钟会**向后跳**,
    /// 跳幅 = 加速期间攒下的虚拟时间 —— 主线程僵住(音频线程不受影响, 表现为"画面冻结、
    /// 声音还在"), 等亏空追平才恢复。本函数直接读被 hook 的 GetTickCount64 把它钉死。
    /// </summary>
    private static void ClockContinuity()
    {
        Console.WriteLine();
        Console.WriteLine("[smoke] --- 时间连续性(切换倍率时虚拟时钟不跳变) ---");

        Apply("4.000");
        Thread.Sleep(300);
        ulong fast0 = VirtualTick();
        Thread.Sleep(400);
        ulong fast1 = VirtualTick();
        True("4x 时虚拟时钟走得更快(400ms 真实 ≈ 1600ms 虚拟, 实测 " + (fast1 - fast0) + "ms)",
             fast1 - fast0 >= 1000);

        // 关键断言: 从 4x 切回 1.0x —— 时钟既不能倒退, 也不能跳变
        Apply("1.000");
        ulong after = VirtualTick();
        True("切回 1.0x 时虚拟时钟不倒退", after >= fast1);
        True("切回 1.0x 时没有大跳变(实测 " + (after - fast1) + "ms)", after - fast1 < 500);

        // 1.0x 必须与真实时间大致同速(offset 只是平移, 不影响斜率)
        ulong normal0 = VirtualTick();
        Thread.Sleep(400);
        ulong normal1 = VirtualTick();
        long normalDelta = (long)(normal1 - normal0);
        True("1.0x 时虚拟时钟与真实时间同速(实测 " + normalDelta + "ms)",
             normalDelta >= 250 && normalDelta < 900);

        // 多来几次来回切换: 每次切回 1.0x 都不能跳变(累积 offset 越大越容易暴露问题)
        for (int round = 0; round < 3; round++)
        {
            Apply((round == 0) ? "4.000" : "7.250");
            Thread.Sleep(300);
            ulong high = VirtualTick();
            Apply("1.000");
            ulong low = VirtualTick();
            True("第 " + (round + 1) + " 轮切回 1.0x 时钟不倒退(实测 " + (low - high) + "ms)",
                 low >= high);
            True("第 " + (round + 1) + " 轮切回 1.0x 没有大跳变(实测 " + (low - high) + "ms)",
                 low - high < 500);
        }

        // 减速请求被拒绝时, 时钟也必须纹丝不动(不能被"半个请求"带跑)
        ulong beforeReject = VirtualTick();
        WriteRequest("0.250");
        Thread.Sleep(400);
        ulong afterReject = VirtualTick();
        long rejectDelta = (long)(afterReject - beforeReject);
        Eq("减速请求 0.250 被拒绝(引擎仍是 1.000)", Read("speed"), "1.000");
        True("被拒绝的减速请求不影响时钟(实测 " + rejectDelta + "ms)",
             rejectDelta >= 250 && rejectDelta < 900);

        // 收尾: 回到测试主流程约定的倍率
        Apply("1.000");
    }

    private static void Apply(string speed)
    {
        WriteRequest(speed);
        long start = RealMs();
        while (RealMs() - start < 3000)
        {
            if (Read("speed") == speed) { Console.WriteLine("  ✓ 请求 " + speed + " 已生效 (" + (RealMs() - start) + "ms)"); return; }
            Thread.Sleep(25);
        }
        Fail("请求 " + speed + " 未在 3 秒内生效");
    }

    private static void WriteRequest(string content) => WriteRequestTo(_request, content);

    private static void WriteRequestTo(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content + "\r\n");
        if (File.Exists(path)) File.Replace(tmp, path, null, true);
        else File.Move(tmp, path);
    }

    private static string Read(string key)
    {
        try
        {
            foreach (var line in File.ReadAllLines(_state))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && line.Substring(0, eq).Trim() == key) return line.Substring(eq + 1).Trim();
            }
        }
        catch { }
        return null;
    }

    private static void Eq(string what, string actual, string expected)
    {
        if (actual == expected) Console.WriteLine("  ✓ " + what);
        else Fail(what + " (期望 " + expected + ", 实际 " + (actual ?? "<null>") + ")");
    }

    private static void True(string what, bool value)
    {
        if (value) Console.WriteLine("  ✓ " + what);
        else Fail(what);
    }

    private static void Fail(string what)
    {
        _failures++;
        Console.WriteLine("  ✗ " + what);
    }

    private static void DumpLog()
    {
        if (!File.Exists(_log)) { Console.WriteLine("[smoke] (没有加载器日志)"); return; }
        Console.WriteLine("[smoke] --- 加载器日志 ---");
        foreach (var line in File.ReadAllLines(_log))
            if (line.Contains("speedctl") || line.Contains("speedhack") || line.Contains("倍率") || line.Contains("基础"))
                Console.WriteLine("        " + line.TrimEnd());
    }
}
