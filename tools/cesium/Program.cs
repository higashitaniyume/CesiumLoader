// cesium - CesiumLoader 模组脚手架与包分发 CLI
// 命令解析由 System.CommandLine 驱动(自动生成帮助/版本/错误提示)。

using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO.Compression;

namespace CesiumCli
{
    internal static class Program
    {
        // 当前 SDK 版本(与 dist 分发一致; 打包时写入 sidecar)
        private const string SdkVersion = "2.0.0";

        private static int Main(string[] args)
        {
            var root = new RootCommand("CesiumLoader 模组脚手架与包分发 CLI");

            // ------------------------------ new ------------------------------
            var newCmd = new Command("new", "生成 mod 项目模板(csproj + ModEntry.cs + AssemblyInfo.cs + sidecar json)");
            var newName = new Argument<string>("name") { Description = "mod 名(合法 C# 标识符, 不含空格/点)" };
            var newOut = new Option<string>("--output", "-o") { Description = "输出目录(默认当前目录)", HelpName = "dir" };
            var newAuthor = new Option<string>("--author") { Description = "作者名" };
            var newDesc = new Option<string>("--desc") { Description = "mod 描述" };
            newCmd.Add(newName);
            newCmd.Add(newOut);
            newCmd.Add(newAuthor);
            newCmd.Add(newDesc);
            newCmd.SetAction((parseResult) =>
            {
                string name = parseResult.GetValue(newName);
                string outDir = parseResult.GetValue(newOut) ?? ".";
                string author = parseResult.GetValue(newAuthor) ?? "";
                string desc = parseResult.GetValue(newDesc) ?? "";

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

                // 定位 SDK DLL: 优先 cesium.exe 同目录(工具包形态), 否则仓库内相对路径
                string sdkDll = null;
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0]);
                if (exeDir != null && File.Exists(Path.Combine(exeDir, "CesiumLoader.SDK.dll")))
                    sdkDll = Path.Combine(exeDir, "CesiumLoader.SDK.dll");
                else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "CesiumLoader.SDK.dll")))
                    sdkDll = Path.Combine(AppContext.BaseDirectory, "CesiumLoader.SDK.dll");

                // 复制 SDK DLL 到项目 refs\ 目录(使项目自包含, 脱离仓库可构建)
                string refsDir = Path.Combine(dir, "refs");
                if (sdkDll != null)
                {
                    Directory.CreateDirectory(refsDir);
                    File.Copy(sdkDll, Path.Combine(refsDir, "CesiumLoader.SDK.dll"), true);
                }

                // 1. csproj
                string csproj;
                if (sdkDll != null)
                {
                    // 工具包形态: SDK 引用项目内 refs\; 游戏热更程序集可选(存在才引用)
                    csproj = $$"""
                        <Project Sdk="Microsoft.NET.Sdk">
                          <PropertyGroup>
                            <TargetFramework>netstandard2.0</TargetFramework>
                            <AssemblyName>{{name}}</AssemblyName>
                            <RootNamespace>{{name}}</RootNamespace>
                            <LangVersion>9.0</LangVersion>
                          </PropertyGroup>
                          <ItemGroup>
                            <!-- CesiumLoader.SDK: 已随项目附带在 refs\ (cesium new 自动复制) -->
                            <Reference Include="CesiumLoader.SDK">
                              <HintPath>refs\CesiumLoader.SDK.dll</HintPath>
                            </Reference>
                            <!-- 游戏热更程序集(可选): mod 直接用游戏类型时, 把 DLL 放到 refs\ 即可自动引用 -->
                            <Reference Include="AstralParty.Runtime" Condition="Exists('refs\AstralParty.Runtime.dll')">
                              <HintPath>refs\AstralParty.Runtime.dll</HintPath>
                              <Private>false</Private>
                            </Reference>
                          </ItemGroup>
                        </Project>
                        """;
                }
                else
                {
                    // 源码形态(仓库内 dotnet run): 回退仓库相对路径, 并提示
                    csproj = $$"""
                        <Project Sdk="Microsoft.NET.Sdk">
                          <PropertyGroup>
                            <TargetFramework>netstandard2.0</TargetFramework>
                            <AssemblyName>{{name}}</AssemblyName>
                            <RootNamespace>{{name}}</RootNamespace>
                            <LangVersion>9.0</LangVersion>
                          </PropertyGroup>
                          <ItemGroup>
                            <!-- CesiumLoader.SDK: 源码形态, 引用仓库内 SDK 项目产物 -->
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
                }
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
                        /// Permissions 只是声明会用到的能力(读对局/操作游戏/写文件),
                        /// 供工具/加载器展示警告 —— 已取消权限门控, 无需申请。
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
                    // Permissions 声明会用到的能力(仅展示/警告, 已取消权限门控):
                    // 声明 GameActions(操作游戏)的 mod 在加载时/工具列表会显示 ⚠️ 警告。
                    [assembly: ModManifest("{{name}}", "1.0.0", "{{author}}", "{{desc}}",
                        Permissions = ModPermission.ReadGameState,
                        SdkVersion = "{{SdkVersion}}")]
                    """;
                File.WriteAllText(Path.Combine(dir, "AssemblyInfo.cs"), asmInfo);

                // 3. sidecar json(加载前就存在, 供依赖解析/版本协商/能力警告; 也随包分发)
                string sidecar = $$"""
                    {"id":"{{name}}","name":"{{name}}","version":"1.0.0","author":"{{author}}","description":"{{desc}}","permissions":1,"sdkVersion":"{{SdkVersion}}","dependencies":[]}
                    """;
                File.WriteAllText(Path.Combine(dir, name + ".json"), sidecar);

                // 4. README 说明
                File.WriteAllText(Path.Combine(dir, "README.md"),
                    $"# {name}\n\n{desc}\n\n由 cesium CLI 生成。`cesium build {name}` 构建, `cesium package {name}` 打包。\n");

                Console.WriteLine($"已生成 mod 项目: {dir}");
                if (sdkDll != null)
                    Console.WriteLine("  SDK 已附带在 refs\\CesiumLoader.SDK.dll (项目自包含)");
                else
                    Console.WriteLine("  ⚠ 未找到 CesiumLoader.SDK.dll, csproj 引用仓库相对路径(源码形态)");
                Console.WriteLine("  下一步: cesium build " + dir);
                return 0;
            });

            // ------------------------------ build ------------------------------
            var buildCmd = new Command("build", "构建 mod (dotnet build)");
            var buildDir = new Argument<string>("dir") { Description = "mod 项目目录(默认当前目录)", Arity = ArgumentArity.ZeroOrOne };
            var buildCfg = new Option<string>("--config", "-c") { Description = "构建配置(默认 Release)", HelpName = "config" };
            buildCmd.Add(buildDir);
            buildCmd.Add(buildCfg);
            buildCmd.SetAction((parseResult) =>
            {
                string dir = parseResult.GetValue(buildDir) ?? ".";
                string config = parseResult.GetValue(buildCfg) ?? "Release";
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
                string dll = Path.Combine(Path.GetDirectoryName(csproj), "bin", config, "netstandard2.0",
                    Path.GetFileNameWithoutExtension(csproj) + ".dll");
                Console.WriteLine($"构建成功: {dll}");
                return 0;
            });

            // ------------------------------ package ------------------------------
            var packageCmd = new Command("package", "打包 mod 为分发 zip(DLL + sidecar)");
            var packageDir = new Argument<string>("dir") { Description = "mod 项目目录(默认当前目录)", Arity = ArgumentArity.ZeroOrOne };
            var packageOut = new Option<string>("--output", "-o") { Description = "输出 zip 路径(默认 <dir>/<name>-1.0.0.zip)", HelpName = "out.zip" };
            packageCmd.Add(packageDir);
            packageCmd.Add(packageOut);
            packageCmd.SetAction((parseResult) =>
            {
                string dir = parseResult.GetValue(packageDir) ?? ".";
                string outZip = parseResult.GetValue(packageOut);
                string name = Path.GetFileName(Path.GetFullPath(dir));
                string dll = Path.Combine(dir, "bin", "Release", "netstandard2.0", name + ".dll");
                if (!File.Exists(dll))
                {
                    Console.Error.WriteLine($"未找到构建产物: {dll} (先运行 cesium build)");
                    return 1;
                }
                if (outZip == null) outZip = Path.Combine(dir, name + "-1.0.0.zip");

                using var zip = ZipFile.Open(outZip, ZipArchiveMode.Create);
                // 包布局: {Name}.dll + {Name}.json(sidecar)
                zip.CreateEntryFromFile(dll, name + ".dll");
                string sidecar = Path.Combine(dir, name + ".json");
                if (File.Exists(sidecar)) zip.CreateEntryFromFile(sidecar, name + ".json");

                Console.WriteLine($"已打包: {outZip}");
                Console.WriteLine("  解压到游戏目录 AstralParty_ModLoader\\mods\\ 即安装完成");
                return 0;
            });

            // ------------------------------ list ------------------------------
            var listCmd = new Command("list", "列出 mods 目录下所有 mod 的元数据(读 sidecar)");
            var listDir = new Argument<string>("mods_dir") { Description = "mods 目录(默认当前目录)", Arity = ArgumentArity.ZeroOrOne };
            listCmd.Add(listDir);
            listCmd.SetAction((parseResult) =>
            {
                string modsDir = parseResult.GetValue(listDir) ?? ".";
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
            });

            // ------------------------------ verify ------------------------------
            var verifyCmd = new Command("verify", "检查依赖完整性与 SDK 版本兼容性(模拟加载器判定)");
            var verifyDir = new Argument<string>("mods_dir") { Description = "mods 目录(默认当前目录)", Arity = ArgumentArity.ZeroOrOne };
            verifyCmd.Add(verifyDir);
            verifyCmd.SetAction((parseResult) =>
            {
                string modsDir = parseResult.GetValue(verifyDir) ?? ".";
                if (!Directory.Exists(modsDir))
                {
                    Console.Error.WriteLine($"目录不存在: {modsDir}");
                    return 1;
                }
                var dlls = Directory.GetFiles(modsDir, "*.dll")
                    .Select(Path.GetFileNameWithoutExtension).ToList();
                var metas = new Dictionary<string, ModMeta>(StringComparer.OrdinalIgnoreCase);
                foreach (var dll in dlls)
                {
                    string sidecar = Path.Combine(modsDir, dll + ".json");
                    if (File.Exists(sidecar))
                        metas[dll] = ParseSidecar(File.ReadAllText(sidecar));
                }
                return VerifySimple(modsDir, dlls, metas);
            });

            root.Add(newCmd);
            root.Add(buildCmd);
            root.Add(packageCmd);
            root.Add(listCmd);
            root.Add(verifyCmd);

            return root.Parse(args).Invoke();
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

        private static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            return name.All(c => char.IsLetterOrDigit(c) || c == '_');
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
