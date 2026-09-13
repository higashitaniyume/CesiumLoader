// cesium - CesiumLoader 模组脚手架与包分发 CLI
//
// 命令:
//   cesium new <Name> [-o <dir>] [--author <名>] [--desc <描述>]
//      生成 mod 项目模板(含 csproj + ModEntry.cs + 自带 sidecar json + 权限配置示例)
//   cesium build <dir> [-c Release]
//      构建 mod (dotnet build)
//   cesium package <dir> [-o <out.zip>]
//      打包 mod 为可分发的 zip(DLL + sidecar + permissions 覆盖示例)
//   cesium list <mods_dir>
//      列出 mods 目录下所有 mod 的元数据(读 sidecar)
//   cesium verify <mods_dir>
//      检查依赖完整性与 SDK 版本兼容性(模拟加载器判定)
//
// 用法示例:
//   cesium new MyMod --author 小明 --desc "我的第一个mod"
//   cesium build MyMod
//   cesium package MyMod -o MyMod-1.0.0.zip
//   cesium verify "C:\...\AstralParty_ModLoader\mods"

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace CesiumCli
{
    internal static class Program
    {
        // 当前 SDK 版本(与 dist 分发一致; 打包时写入 sidecar)
        private const string SdkVersion = "2.0.0";

        private static int Main(string[] args)
        {
            if (args.Length == 0) { PrintUsage(); return 1; }
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "new": return New(args.Skip(1).ToArray());
                    case "build": return Build(args.Skip(1).ToArray());
                    case "package": return Package(args.Skip(1).ToArray());
                    case "list": return List(args.Skip(1).ToArray());
                    case "verify": return Verify(args.Skip(1).ToArray());
                    case "help": case "-h": case "--help": PrintUsage(); return 0;
                    default:
                        Console.Error.WriteLine($"未知命令: {args[0]}");
                        PrintUsage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"错误: {ex.Message}");
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("""
                cesium - CesiumLoader 模组脚手架与包分发 CLI v2.0.0

                用法:
                  cesium new <Name> [-o <dir>] [--author <名>] [--desc <描述>]
                      生成 mod 项目模板
                  cesium build <dir> [-c Release]
                      构建 mod (dotnet build)
                  cesium package <dir> [-o <out.zip>]
                      打包 mod 为分发 zip
                  cesium list <mods_dir>
                      列出 mods 目录的 mod 元数据
                  cesium verify <mods_dir>
                      检查依赖/版本兼容性

                示例:
                  cesium new MyMod --author 小明
                  cesium build MyMod
                  cesium package MyMod -o MyMod-1.0.0.zip
                """);
        }

        // ============================== new ==============================

        private static int New(string[] args)
        {
            if (args.Length == 0 || args[0].StartsWith("-"))
            {
                Console.Error.WriteLine("用法: cesium new <Name> [-o <dir>] [--author <名>] [--desc <描述>]");
                return 1;
            }
            string name = args[0];
            string outDir = ".";
            string author = "";
            string desc = "";
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-o": if (i + 1 < args.Length) outDir = args[++i]; break;
                    case "--author": if (i + 1 < args.Length) author = args[++i]; break;
                    case "--desc": if (i + 1 < args.Length) desc = args[++i]; break;
                    default: Console.Error.WriteLine($"忽略未知参数: {args[i]}"); break;
                }
            }

            // 名字校验: 合法 C# 标识符 + 程序集名
            if (!IsValidName(name))
            {
                Console.Error.WriteLine($"非法 mod 名 '{name}': 须为字母/数字/下划线开头字母, 不含空格和点");
                return 1;
            }

            string dir = Path.Combine(outDir, name);
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Console.Error.WriteLine($"目录已存在且非空: {dir}");
                return 1;
            }
            Directory.CreateDirectory(dir);

            // 1. csproj
            string csproj = $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.0</TargetFramework>
                    <AssemblyName>{{name}}</AssemblyName>
                    <RootNamespace>{{name}}</RootNamespace>
                    <LangVersion>9.0</LangVersion>
                  </PropertyGroup>
                  <ItemGroup>
                    <!-- 引用 CesiumLoader.SDK: 改为你的 SDK 路径 -->
                    <Reference Include="CesiumLoader.SDK">
                      <HintPath>..\..\..\src\CesiumLoader.SDK\bin\Release\netstandard2.0\CesiumLoader.SDK.dll</HintPath>
                    </Reference>
                    <!-- 游戏热更程序集: 从游戏目录或本地副本引用 -->
                    <Reference Include="AstralParty.Runtime">
                      <HintPath>..\..\refs\AstralParty.Runtime.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(dir, name + ".csproj"), csproj);

            // 2. ModEntry.cs
            string entry = $$"""
                using System;
                using CesiumLoader.SDK;

                namespace {{name}}
                {
                    /// <summary>
                    /// {{name}} —— 由 cesium CLI 生成的 mod 模板。
                    ///
                    /// 元数据在 AssemblyInfo.cs(程序集级声明, 权威位置)。
                    /// 敏感能力(GameActions/SpeedHack)默认关闭, 需要时在
                    /// AssemblyInfo.cs 的 Permissions 中声明, 并在文档中说明用途。
                    /// </summary>
                    public static class ModEntry
                    {
                        public static void Main()
                        {
                            SdkManifest.ExportSidecar();  // 生成/刷新 sidecar(幂等)
                            ModBase.Run(OnInit);          // 纯事件驱动: 无 tick 不轮询
                        }

                        private static void OnInit()
                        {
                            SdkLog.Info("{{name}}", "=== {{name}} 初始化 ===");

                            // 事件驱动示例: 订阅事件 + 自动挂钩(无需每秒轮询)
                            GameEvents.CardUsed += (pid, cardId, remain) =>
                                SdkLog.Info("{{name}}", $"玩家 {pid} 出牌 {Names.Card(cardId)} (剩{remain}张)");
                            GameEvents.StartAutoHook();
                        }
                    }
                }
                """;
            File.WriteAllText(Path.Combine(dir, "ModEntry.cs"), entry);

            // 2b. AssemblyInfo.cs(程序集级元数据声明, 权威位置)
            string asmInfo = $$"""
                using CesiumLoader.SDK;

                // mod 元数据: 程序集级声明(权威位置, 读取时不触发类型加载, 兼容 HybridCLR)。
                // 敏感权限(GameActions/SpeedHack)默认关闭, 需要时在此声明,
                // 并可用 mods\{程序集名}.permissions.json 逐项覆盖。
                [assembly: ModManifest("{{name}}", "1.0.0", "{{author}}", "{{desc}}",
                    Permissions = ModPermission.ReadGameState,
                    SdkVersion = "{{SdkVersion}}")]
                """;
            File.WriteAllText(Path.Combine(dir, "AssemblyInfo.cs"), asmInfo);

            // 3. sidecar json(加载前就存在, 供依赖解析/版本协商; 也随包分发)
            string sidecar = $$"""
                {"id":"{{name}}","name":"{{name}}","version":"1.0.0","author":"{{author}}","description":"{{desc}}","permissions":1,"sdkVersion":"{{SdkVersion}}","dependencies":[]}
                """;
            File.WriteAllText(Path.Combine(dir, name + ".json"), sidecar);

            // 4. 权限覆盖示例(敏感能力开关)
            string permExample = $$"""
                {
                  // 权限覆盖示例: 把 {{name}} 的敏感权限显式授予/拒绝。
                  // 复制到 mods\{{name}}.permissions.json 生效(游戏重启后)。
                  // 可用权限: GameActions / SpeedHack / ReadGameState / FileWrite
                  "{{name}}": {
                    "GameActions": false,
                    "SpeedHack": false
                  }
                }
                """;
            File.WriteAllText(Path.Combine(dir, name + ".permissions.example.json"), permExample);

            // 5. README 说明
            File.WriteAllText(Path.Combine(dir, "README.md"),
                $"# {name}\n\n{desc}\n\n由 cesium CLI 生成。`cesium build {name}` 构建, `cesium package {name}` 打包。\n");

            Console.WriteLine($"已生成 mod 项目: {dir}");
            Console.WriteLine("  下一步: cesium build " + dir);
            return 0;
        }

        private static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            return name.All(c => char.IsLetterOrDigit(c) || c == '_');
        }

        // ============================== build ==============================

        private static int Build(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : ".";
            string config = "Release";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "-c" && i + 1 < args.Length) config = args[++i];
            }
            string csproj = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.csproj").FirstOrDefault()
                : null;
            if (csproj == null)
            {
                Console.Error.WriteLine($"未找到 csproj: {dir}");
                return 1;
            }
            Console.WriteLine($"构建 {csproj} ({config})...");
            int code = RunProcess("dotnet", $"build \"{csproj}\" -c {config} --nologo");
            if (code != 0)
            {
                Console.Error.WriteLine("构建失败");
                return code;
            }
            // 输出产物位置
            string dll = Path.Combine(Path.GetDirectoryName(csproj), "bin", config, "netstandard2.0",
                Path.GetFileNameWithoutExtension(csproj) + ".dll");
            Console.WriteLine($"构建成功: {dll}");
            return 0;
        }

        // ============================== package ==============================

        private static int Package(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : ".";
            string outZip = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "-o" && i + 1 < args.Length) outZip = args[++i];
            }
            string name = Path.GetFileName(Path.GetFullPath(dir));
            string dll = Path.Combine(dir, "bin", "Release", "netstandard2.0", name + ".dll");
            if (!File.Exists(dll))
            {
                Console.Error.WriteLine($"未找到构建产物: {dll} (先运行 cesium build)");
                return 1;
            }
            if (outZip == null) outZip = Path.Combine(dir, name + "-1.0.0.zip");

            using var zip = ZipFile.Open(outZip, ZipArchiveMode.Create);
            // 包布局: {Name}.dll + {Name}.json(sidecar) + {Name}.permissions.json(可选)
            zip.CreateEntryFromFile(dll, name + ".dll");
            string sidecar = Path.Combine(dir, name + ".json");
            if (File.Exists(sidecar)) zip.CreateEntryFromFile(sidecar, name + ".json");
            string perm = Path.Combine(dir, name + ".permissions.json");
            if (File.Exists(perm)) zip.CreateEntryFromFile(perm, name + ".permissions.json");

            Console.WriteLine($"已打包: {outZip}");
            Console.WriteLine("  解压到游戏目录 AstralParty_ModLoader\\mods\\ 即安装完成");
            return 0;
        }

        // ============================== list ==============================

        private static int List(string[] args)
        {
            string modsDir = args.Length > 0 ? args[0] : ".";
            if (!Directory.Exists(modsDir))
            {
                Console.Error.WriteLine($"目录不存在: {modsDir}");
                return 1;
            }
            var dlls = Directory.GetFiles(modsDir, "*.dll")
                .Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToList();
            if (dlls.Count == 0) { Console.WriteLine("(无 mod)"); return 0; }

            Console.WriteLine($"mods 目录: {modsDir}");
            Console.WriteLine($"{"ID",-24} {"版本",-10} {"SDK",-10} 权限  依赖");
            foreach (var dll in dlls)
            {
                string sidecar = Path.Combine(modsDir, dll + ".json");
                if (!File.Exists(sidecar))
                {
                    Console.WriteLine($"{dll,-24} (无 sidecar)");
                    continue;
                }
                var meta = ParseSidecar(File.ReadAllText(sidecar));
                string deps = meta.Deps.Count == 0 ? "-" : string.Join(",", meta.Deps.Select(d => $"{d.Id}{(d.MinVersion == null ? "" : ">=" + d.MinVersion)}"));
                Console.WriteLine($"{meta.Id,-24} {meta.Version,-10} {(meta.SdkVersion ?? ""),-10} {meta.Permissions,6}  {deps}");
            }
            return 0;
        }

        // ============================== verify ==============================

        private static int Verify(string[] args)
        {
            string modsDir = args.Length > 0 ? args[0] : ".";
            if (!Directory.Exists(modsDir))
            {
                Console.Error.WriteLine($"目录不存在: {modsDir}");
                return 1;
            }
            var dlls = Directory.GetFiles(modsDir, "*.dll")
                .Select(Path.GetFileNameWithoutExtension).ToList();
            var metas = new Dictionary<string, ModMeta>();
            foreach (var dll in dlls)
            {
                string sidecar = Path.Combine(modsDir, dll + ".json");
                if (File.Exists(sidecar))
                    metas[dll] = ParseSidecar(File.ReadAllText(sidecar));
            }
            return VerifySimple(modsDir, dlls, metas);
        }

        // 简化版 verify: 清晰的逐项输出
        private static int VerifySimple(string modsDir, List<string> dlls, Dictionary<string, ModMeta> metas)
        {
            Console.WriteLine($"验证 mods 目录: {modsDir} (SDK 版本 {SdkVersion})");
            Console.WriteLine();
            bool anyError = false;
            foreach (var dll in dlls.OrderBy(x => x))
            {
                if (!metas.TryGetValue(dll, out var meta))
                {
                    Console.WriteLine($"[跳过] {dll}  无 sidecar(旧 mod, 兼容)");
                    continue;
                }
                var issues = new List<string>();
                if (!string.IsNullOrEmpty(meta.SdkVersion) && Compare(meta.SdkVersion, SdkVersion) > 0)
                    issues.Add($"需要 SDK {meta.SdkVersion} > 当前 {SdkVersion}");
                foreach (var dep in meta.Deps)
                {
                    if (!dlls.Contains(dep.Id, StringComparer.OrdinalIgnoreCase))
                        issues.Add($"缺失依赖 {dep.Id}");
                    else if (dep.MinVersion != null && metas.TryGetValue(dep.Id, out var depMeta) &&
                             Compare(depMeta.Version, dep.MinVersion) < 0)
                        issues.Add($"依赖 {dep.Id} 版本过低(需 {dep.MinVersion}, 有 {depMeta.Version})");
                }
                if (issues.Count > 0)
                {
                    Console.WriteLine($"[拒绝] {dll}  {string.Join("; ", issues)}");
                    anyError = true;
                }
                else
                {
                    string deps = meta.Deps.Count == 0 ? "-" : string.Join(",", meta.Deps.Select(d => d.Id));
                    Console.WriteLine($"[通过] {dll}  v{meta.Version}  依赖: {deps}");
                }
            }
            Console.WriteLine();
            Console.WriteLine(anyError ? "存在不兼容项(加载器会跳过被拒 mod)" : "全部兼容 ✓");
            return anyError ? 2 : 0;
        }

        // ============================== 工具 ==============================

        private static int RunProcess(string exe, string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                UseShellExecute = false
            };
            var p = System.Diagnostics.Process.Start(psi);
            p.WaitForExit();
            return p.ExitCode;
        }

        private class ModMeta
        {
            public string Id;
            public string Version;
            public string SdkVersion;
            public int Permissions;
            public List<(string Id, string MinVersion)> Deps = new();
        }

        // 极简 sidecar 解析(与加载器同格式)
        private static ModMeta ParseSidecar(string json)
        {
            var m = new ModMeta();
            m.Id = JStr(json, "id") ?? JStr(json, "name");
            m.Version = JStr(json, "version");
            m.SdkVersion = JStr(json, "sdkVersion");
            string p = JVal(json, "permissions");
            int.TryParse(p, out m.Permissions);
            // dependencies
            string depSec = JVal(json, "dependencies");
            if (depSec != null && depSec.StartsWith("["))
            {
                int i = 0;
                while (true)
                {
                    int idq = depSec.IndexOf("\"id\"", i, StringComparison.Ordinal);
                    if (idq < 0) break;
                    int colon = depSec.IndexOf(':', idq);
                    if (colon < 0) break;
                    int v1 = depSec.IndexOf('"', colon + 1);
                    if (v1 < 0) break;
                    int v2 = depSec.IndexOf('"', v1 + 1);
                    if (v2 < 0) break;
                    string id = depSec.Substring(v1 + 1, v2 - v1 - 1);
                    string minV = null;
                    int braceEnd = depSec.IndexOf('}', v2);
                    if (braceEnd < 0) break;   // 防死循环
                    int mq = depSec.IndexOf("\"minVersion\"", v2, StringComparison.Ordinal);
                    if (mq >= 0 && mq < braceEnd)
                    {
                        int mc = depSec.IndexOf(':', mq);
                        int m1 = mc < 0 ? -1 : depSec.IndexOf('"', mc + 1);
                        int m2 = m1 < 0 ? -1 : depSec.IndexOf('"', m1 + 1);
                        if (m2 > 0) minV = depSec.Substring(m1 + 1, m2 - m1 - 1);
                    }
                    m.Deps.Add((id, minV));
                    int next = braceEnd + 1;
                    if (next <= i) break;      // 防死循环: 位置未前进
                    i = next;
                }
            }
            return m;
        }

        private static string JStr(string json, string key)
        {
            string v = JVal(json, key);
            if (v == null || !v.StartsWith("\"")) return null;
            return v.Trim('"');
        }

        private static string JVal(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int pos = json.IndexOf(needle, StringComparison.Ordinal);
            if (pos < 0) return null;
            int colon = json.IndexOf(':', pos);
            if (colon < 0) return null;
            int s = colon + 1;
            while (s < json.Length && char.IsWhiteSpace(json[s])) s++;
            if (s >= json.Length) return null;
            // 字符串 → 到下一个未转义引号; 否则 → 到 , 或 }
            if (json[s] == '"')
            {
                int e = s + 1;
                while (e < json.Length && json[e] != '"')
                {
                    if (json[e] == '\\') e++;
                    e++;
                }
                return json.Substring(s, Math.Min(e + 1, json.Length) - s);
            }
            int end = s;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']') end++;
            return json.Substring(s, end - s).Trim();
        }

        private static int Compare(string a, string b)
        {
            int[] pa = Parse(a), pb = Parse(b);
            for (int i = 0; i < 3; i++)
                if (pa[i] != pb[i]) return pa[i] > pb[i] ? 1 : -1;
            return 0;
        }

        private static int[] Parse(string v)
        {
            var r = new int[3];
            if (string.IsNullOrEmpty(v)) return r;
            var parts = v.Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                int n;
                if (int.TryParse(parts[i], out n)) r[i] = n;
            }
            return r;
        }
    }
}
