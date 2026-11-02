using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AudioSkinMod;
using Xunit;

namespace AudioSkinMod.Tests
{
    public class BclCompatibilityTests
    {
        [Fact]
        public void BuiltMod_DoesNotReferenceKnownUnsupportedHybridClrMembers()
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, "AudioSkinMod.dll");
            using var stream = File.OpenRead(dllPath);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.MemberReferences)
            {
                var member = reader.GetMemberReference(handle);
                if (member.Parent.Kind != HandleKind.TypeReference) continue;
                var declaringType = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                string type = reader.GetString(declaringType.Namespace) + "." + reader.GetString(declaringType.Name);
                string method = reader.GetString(member.Name);
                Assert.False(type == "System.AppDomain" && method == "get_BaseDirectory", $"{type}.{method} is not supported in HybridCLR");
                Assert.False(type == "System.Reflection.Assembly" && method == "get_Location", $"{type}.{method} is not supported in HybridCLR");
                Assert.False(type == "System.IO.FileInfo" || type == "System.IO.DirectoryInfo", $"{type}.{method} is not supported in HybridCLR");
                Assert.False(type == "System.IO.FileStream" || type == "System.IO.StreamReader" || type == "System.IO.StreamWriter", $"{type}.{method} is not supported in HybridCLR");
            }
        }
    }
}
