using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CesiumLoader.Bootstrap
{
    /// <summary>
    /// Doorstop 式托管引导程序。
    ///
    /// 由原生代理(version.dll)在 HybridCLR 热更就绪后, 通过
    /// Assembly.Load(byte[]) 加载本程序集并调用 Bootstrap.Main()。
    ///
    /// 职责(从原生 loader.cpp 迁移而来, 全部在 C# 里完成, 可脱离游戏单元测试):
    ///   1. 按文件名排序加载 sdk\*.dll(不调入口, 供 mod 引用)
    ///   2. 按文件名排序加载 mods\*.dll 并调用 {文件名}.ModEntry.Main()
    ///   3. 全程 try/catch, 单个 mod 失败不中断其他 mod
    ///
    /// 目录来自环境变量(由原生代理设置):
    ///   CESIUM_SDK_DIR / CESIUM_MODS_DIR / CESIUM_LOG_DIR
    /// 未设置时回退到 %LocalAppData%\AstralParty_ModLoader\ 下对应子目录。
    /// </summary>
    public static class Bootstrap
    {
        private static readonly object _logLock = new object();
        private static string _logFile;

        // ---------- 日志(与 SdkLog 共用 activity-mod.log, 由原生转发线程输出到控制台) ----------

        private static void Log(string line)
        {
            try
            {
                lock (_logLock)
                {
                    if (_logFile == null)
                    {
                        string dir = EnvOr("CESIUM_LOG_DIR", Path.Combine(LocalAppData(), "AstralParty_ModLoader", "logs"));
                        Directory.CreateDirectory(dir);
                        _logFile = Path.Combine(dir, "activity-mod.log");
                    }
                    File.AppendAllText(_logFile, $"[{DateTime.Now:HH:mm:ss.fff}] [INF] [Bootstrap] {line}\r\n");
                }
            }
            catch { }
        }

        // ---------- 入口 ----------

        public static void Main()
        {
            try
            {
                MainSafe();
            }
            catch (Exception e)
            {
                Log("引导程序致命异常: " + e);
            }
        }

        private static void MainSafe()
        {
            Log("=== CesiumLoader Bootstrap (Doorstop) 启动 ===");

            string sdkDir = EnvOr("CESIUM_SDK_DIR", Path.Combine(LocalAppData(), "AstralParty_ModLoader", "sdk"));
            string modsDir = EnvOr("CESIUM_MODS_DIR", Path.Combine(LocalAppData(), "AstralParty_ModLoader", "mods"));

            // 1. SDK: 按文件名排序, 全部先加载(不调入口)
            int sdkLoaded = 0;
            foreach (string dll in SortedDlls(sdkDir))
            {
                string name = Path.GetFileName(dll);
                try
                {
                    Assembly.Load(File.ReadAllBytes(dll));
                    sdkLoaded++;
                    Log("SDK 加载成功: " + name);
                }
                catch (Exception e)
                {
                    Log("SDK 加载失败 " + name + ": " + e.Message);
                }
            }
            Log($"SDK 加载完成: {sdkLoaded} 成功 / {CountDlls(sdkDir)} 个");

            // 2. Mods: 按文件名排序, 逐个加载 + 调用入口
            if (!Directory.Exists(modsDir))
            {
                Log("mods 目录不存在: " + modsDir);
                return;
            }

            int modOk = 0, modFail = 0;
            foreach (string dll in SortedDlls(modsDir))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                string entryType = name + ".ModEntry";
                try
                {
                    Assembly asm = Assembly.Load(File.ReadAllBytes(dll));
                    Log("Mod 加载成功: " + Path.GetFileName(dll));

                    // HybridCLR/IL2CPP AOT 裁剪: Assembly.GetType(string,bool) 可能不存在
                    // (MethodNotFind System.Reflection.Assembly::GetType), 因此用多级 fallback。
                    Type type = FindEntryType(asm, entryType);
                    if (type == null)
                    {
                        Log("Mod 入口类型不存在: " + entryType);
                        modFail++;
                        continue;
                    }

                    // 同理, GetMethod 也可能受限: 优先精确签名, fallback 到遍历
                    MethodInfo method = FindMainMethod(type);
                    if (method == null)
                    {
                        Log("Mod 入口方法不存在: " + entryType + ".Main()");
                        modFail++;
                        continue;
                    }

                    try
                    {
                        method.Invoke(null, null);
                        modOk++;
                        Log("Mod 入口执行成功: " + name);
                    }
                    catch (Exception e)
                    {
                        // 入口内部抛异常: 记录最内层异常, 继续下一个 mod
                        Exception inner = Unwrap(e);
                        modFail++;
                        Log("Mod 入口异常 " + name + ": " + inner.GetType().Name + ": " + inner.Message);
                    }
                }
                catch (Exception e)
                {
                    modFail++;
                    Log("Mod 加载失败 " + Path.GetFileName(dll) + ": " + e.Message);
                }
            }
            Log($"=== Bootstrap 引导结束: {modOk} 成功 / {modFail} 失败 ===");
        }

        // ---------- HybridCLR 反射 fallback ----------
        //
        // IL2CPP AOT 会裁剪部分反射 API。已知不可用: Assembly.GetType(string, bool)
        // (MethodNotFind System.Reflection.Assembly::GetType)。
        // 按顺序尝试多种定位方式, 每个都独立 try/catch 并记录日志。

        private static Type FindEntryType(Assembly asm, string fullName)
        {
            // 1. 直接 GetType(1 参) —— 先试不带 bool 的重载
            try
            {
                Type t = asm.GetType(fullName);
                if (t != null) { Log("入口类型: GetType(1参) 命中 " + fullName); return t; }
            }
            catch (Exception e) { Log("入口类型 GetType(1参) 失败: " + e.GetType().Name + ": " + e.Message); }

            // 2. 遍历 GetTypes() —— HomeBackgroundMod 验证过的 fallback
            try
            {
                foreach (Type t in asm.GetTypes())
                {
                    if (t.FullName == fullName)
                    {
                        Log("入口类型: GetTypes() 命中 " + fullName);
                        return t;
                    }
                }
                Log("入口类型: GetTypes() 未找到 " + fullName);
            }
            catch (Exception e) { Log("入口类型 GetTypes() 失败: " + e.GetType().Name + ": " + e.Message); }

            // 3. Type.GetType(带程序集名) —— static 形式
            try
            {
                Type t = Type.GetType(fullName + ", " + asm.GetName().Name, false);
                if (t != null) { Log("入口类型: Type.GetType 命中 " + fullName); return t; }
            }
            catch (Exception e) { Log("入口类型 Type.GetType 失败: " + e.GetType().Name + ": " + e.Message); }

            return null;
        }

        private static MethodInfo FindMainMethod(Type type)
        {
            // 1. 精确签名(无参数)
            try
            {
                MethodInfo m = type.GetMethod("Main", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m != null) { Log("入口方法: 精确签名命中 Main()"); return m; }
            }
            catch (Exception e) { Log("入口方法 精确签名失败: " + e.GetType().Name + ": " + e.Message); }

            // 2. 遍历方法
            try
            {
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "Main" && m.GetParameters().Length == 0)
                    {
                        Log("入口方法: 遍历命中 Main()");
                        return m;
                    }
                }
            }
            catch (Exception e) { Log("入口方法 遍历失败: " + e.GetType().Name + ": " + e.Message); }

            return null;
        }

        // ---------- 辅助 ----------

        private static Exception Unwrap(Exception e)
        {
            Exception cur = e;
            while (cur is TargetInvocationException && cur.InnerException != null)
                cur = cur.InnerException;
            return cur;
        }

        private static string LocalAppData()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
            catch { return "."; }
        }

        private static string EnvOr(string name, string fallback)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static IEnumerable<string> SortedDlls(string dir)
        {
            if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
            return Directory.GetFiles(dir, "*.dll")
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        }

        private static int CountDlls(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "*.dll").Length;
        }
    }
}
