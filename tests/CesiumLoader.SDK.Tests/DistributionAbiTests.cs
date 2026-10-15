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
            using var gameStream = File.OpenRead(Path.GetFullPath(Path.Combine(Repo, "..", "..", "extracted_dlls", "AstralParty.Runtime.dll")));
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
            var path = Path.Combine(Repo, "dist", "modloader", "AstralParty_ModLoader", relative);
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            var type = md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t => md.GetString(t.Name) == typeName);
            var method = type.GetMethods().Select(md.GetMethodDefinition).Single(m => md.GetString(m.Name) == methodName);
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
            var root = Path.Combine(Repo, "dist", "modloader", "AstralParty_ModLoader");
            var mods = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Repo, "tools", "builtin-mods.json")));
            var paths = new List<string> { Path.Combine(root, "sdk", "CesiumLoader.SDK.dll") };
            paths.AddRange(mods.Select(m => Path.Combine(root, "mods", m, m + ".dll")));
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
