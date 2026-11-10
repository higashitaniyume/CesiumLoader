using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    // Inspect shipped IL without loading Unity/game code into the test runtime.
    // Enum constants are inlined: testing only the source enum misses stale dist DLLs.
    public class DistributionAbiTests
    {
        private static string Repo
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools", "builtin-mods.json")))
                    dir = dir.Parent;
                return dir?.FullName ?? throw new DirectoryNotFoundException("CesiumLoader repository");
            }
        }

        private static string GameRuntime
        {
            get
            {
                var configured = typeof(DistributionAbiTests).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "GameRuntimeAssemblyPath")?.Value;
                if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
                var fallback = Path.Combine(Repo, "refs", "game", "AstralParty.Runtime.dll");
                if (File.Exists(fallback)) return fallback;
                throw new FileNotFoundException(
                    "找不到游戏热更程序集: 期望 refs/game/AstralParty.Runtime.dll (见 refs/game/README.md)");
            }
        }

        // 分发产物优先取打包好的 dist 布局(与用户实际拿到的文件一致); 没有就取源码构建输出,
        // 这样干净 clone 后直接 `dotnet test` 也能跑, 不依赖仓库里预先放好的 dll。
        private static string StagedDist => Path.Combine(Repo, "dist", "modloader", "AstralParty_ModLoader");

        private static string ResolveShipped(string relative) =>
            ResolveShippedOrNull(relative) ?? throw new FileNotFoundException(
                $"找不到分发程序集 {relative}: 既没有 dist 打包产物, 也没有源码构建输出。先构建对应项目" +
                "(或运行 tools/package-modloader.ps1 生成完整 dist 布局)。");

        private static string ResolveShippedOrNull(string relative)
        {
            var staged = Path.Combine(StagedDist, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(staged)) return staged;

            var segments = relative.Split('/');
            string projectDir, projectName;
            if (segments[0] == "sdk")
            {
                projectDir = Path.Combine(Repo, "loader", "CesiumLoader.SDK");
                projectName = "CesiumLoader.SDK.dll";
            }
            else if (segments[0] == "mods")
            {
                projectDir = Path.Combine(Repo, "mods", segments[1]);
                projectName = segments[2];
            }
            else
            {
                throw new ArgumentException("未知的分发路径: " + relative);
            }

            var bin = Path.Combine(projectDir, "bin");
            return Directory.Exists(bin)
                ? Directory.GetFiles(bin, projectName, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
        }

        [Theory]
        [InlineData("sdk/CesiumLoader.SDK.dll", "Names", "ResolveCard", "GetLocal", "Card,Skill")]
        [InlineData("sdk/CesiumLoader.SDK.dll", "Names", "Relic", "GetLocal", "Relic")]
        [InlineData("sdk/CesiumLoader.SDK.dll", "Names", "Divination", "GetLocal", "Divination")]
        [InlineData("sdk/CesiumLoader.SDK.dll", "Names", "Skill", "GetLocal", "Skill")]
        [InlineData("mods/CardSkinMod/CardSkinMod.dll", "CardSkinApplier", "InjectTextureManager", "GetName", "Card,Event,Destiny,MapEvent")]
        [InlineData("mods/CardSkinMod/CardSkinMod.dll", "CardSkinApplier", "ApplyToGallery", "GetName", "Card,Event")]
        [InlineData("mods/CardSkinMod/CardSkinMod.dll", "CardSkinApplier", "ApplyToCardButton", "GetName", "Card")]
        public void ShippedLocalizationCallsUseCurrentGameEnum(string relative, string typeName,
            string methodName, string target, string enumNames)
        {
            using var gameStream = File.OpenRead(GameRuntime);
            using var game = new PEReader(gameStream);
            var gm = game.GetMetadataReader();
            var enumType = gm.TypeDefinitions.Select(gm.GetTypeDefinition)
                .Single(t => gm.GetString(t.Name) == "UIStringType");
            var values = new Dictionary<string, int>();
            foreach (var handle in enumType.GetFields())
            {
                var field = gm.GetFieldDefinition(handle);
                if (field.GetDefaultValue().IsNil) continue;
                var constant = gm.GetConstant(field.GetDefaultValue());
                values[gm.GetString(field.Name)] = gm.GetBlobReader(constant.Value).ReadInt32();
            }
            var expected = enumNames.Split(',').Select(n => values[n]).ToArray();
            var path = ResolveShipped(relative);
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            var type = md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t => md.GetString(t.Name) == typeName);
            // Relic/Skill now have forwarding one-argument overloads; inspect the implementation.
            var method = type.GetMethods().Select(md.GetMethodDefinition).Single(m => md.GetString(m.Name) == methodName &&
                (methodName is not ("Relic" or "Skill") || m.GetParameters().Count(p => md.GetParameter(p).SequenceNumber > 0) == 2));
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
            var actual = new List<int>();
            int? previousConstant = null;
            foreach (var instruction in Decode(il))
            {
                if (instruction.Op == OpCodes.Call || instruction.Op == OpCodes.Callvirt)
                {
                    var handle = MetadataTokens.EntityHandle(instruction.Operand);
                    string name = handle.Kind == HandleKind.MemberReference
                        ? md.GetString(md.GetMemberReference((MemberReferenceHandle)handle).Name)
                        : handle.Kind == HandleKind.MethodDefinition
                            ? md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)handle).Name) : "";
                    if (name == target)
                    {
                        Assert.True(previousConstant.HasValue, $"{relative}: {methodName} must supply a constant to {target}");
                        actual.Add(previousConstant.Value);
                    }
                }
                previousConstant = instruction.Constant;
            }
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ShippedManagedDllsHaveMatchingPortableSymbols()
        {
            var mods = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Repo, "tools", "builtin-mods.json")));
            var paths = new List<string>();
            if (File.Exists(Path.Combine(StagedDist, "sdk", "CesiumLoader.SDK.dll")))
            {
                // 打包布局已刷新(CI 与 tools/package-modloader.ps1 都会先产出): 严格校验每个随包程序集,
                // 少一个就说明清单/打包步骤落了东西。
                var expected = new List<string> { Path.Combine(StagedDist, "sdk", "CesiumLoader.SDK.dll") };
                expected.AddRange(mods.Select(m => Path.Combine(StagedDist, "mods", m, m + ".dll")));
                foreach (var path in expected)
                    Assert.True(File.Exists(path), $"dist 分发布局缺少 {path}");
                paths.AddRange(expected);
            }
            else
            {
                // 只有源码构建输出: 校验已构建的产物。SDK 与 CardSkinMod 由 ProjectReference 保证存在,
                // 其余内置 mod 构建过就一起校验。
                paths.Add(ResolveShipped("sdk/CesiumLoader.SDK.dll"));
                foreach (var mod in mods)
                {
                    var path = ResolveShippedOrNull($"mods/{mod}/{mod}.dll");
                    if (path != null) paths.Add(path);
                }
                Assert.True(paths.Count > 1, "至少应校验 SDK 与 CardSkinMod 的符号; 先构建对应项目");
            }

            foreach (var path in paths)
            {
                using var dllStream = File.OpenRead(path);
                using var pe = new PEReader(dllStream);
                var entry = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
                var guid = pe.ReadCodeViewDebugDirectoryData(entry).Guid;
                using var pdbStream = File.OpenRead(Path.ChangeExtension(path, ".pdb"));
                using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                var id = provider.GetMetadataReader().DebugMetadataHeader.Id;
                Assert.Equal(guid, new Guid(id.Take(16).ToArray()));
            }
        }

        // SDK 的 XML 文档必须与 dll 同名同目录随发布包分发, 否则第三方 mod 作者在 IDE 里
        // 引用 SDK 时看不到任何注释。这里同时确认文档里确实有成员, 而不是一个空壳。
        [Fact]
        public void ShippedSdkIncludesXmlDocumentation()
        {
            var dll = ResolveShipped("sdk/CesiumLoader.SDK.dll");
            var xmlPath = Path.ChangeExtension(dll, ".xml");
            Assert.True(File.Exists(xmlPath), $"SDK 缺少 XML 文档: {xmlPath} (需与 dll 同名同目录)");

            var members = XDocument.Load(xmlPath).Root?.Element("members")?.Elements("member").ToList()
                          ?? new List<XElement>();
            Assert.True(members.Count >= 100, $"SDK XML 文档成员过少: {members.Count}");

            var names = members.Select(m => (string)m.Attribute("name")).Where(n => n != null).ToList();
            Assert.Contains(names, n => n.StartsWith("T:CesiumLoader.SDK."));
            Assert.Contains(names, n => n.StartsWith("M:CesiumLoader.SDK."));
        }

        private static IEnumerable<(OpCode Op, int Operand, int? Constant)> Decode(byte[] bytes)
        {
            var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null))
                .ToDictionary(o => unchecked((ushort)o.Value));
            int pos = 0;
            while (pos < bytes.Length)
            {
                ushort key = bytes[pos++];
                if (key == 0xfe) key = (ushort)(0xfe00 | bytes[pos++]);
                var op = opcodes[key];
                int size = op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, pos),
                    _ => 4
                };
                int operand = size == 4 ? BitConverter.ToInt32(bytes, pos) : size == 1 ? (sbyte)bytes[pos] : 0;
                int? constant = op.Value >= OpCodes.Ldc_I4_M1.Value && op.Value <= OpCodes.Ldc_I4_8.Value
                    ? op.Value - OpCodes.Ldc_I4_0.Value
                    : op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S ? operand : null;
                pos += size;
                yield return (op, operand, constant);
            }
        }
    }
}
