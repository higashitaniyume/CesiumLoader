using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using CardSkinMod;
using Xunit;

namespace CardSkinMod.Tests
{
    public class BclCompatibilityTests
    {
        [Fact]
        public void BuiltMod_DoesNotReferenceKnownUnsupportedHybridClrMembers()
        {
            // 检查编译后的引用，避免“不执行的兜底分支”仍携带不兼容方法 token。
            using var stream = File.OpenRead(typeof(CardSkinModel).Assembly.Location);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.MemberReferences)
            {
                var member = reader.GetMemberReference(handle);
                if (member.Parent.Kind != HandleKind.TypeReference) continue;
                var declaringType = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                string type = reader.GetString(declaringType.Namespace) + "." + reader.GetString(declaringType.Name);
                string method = reader.GetString(member.Name);
                Assert.False(type == "System.AppDomain" && method == "get_BaseDirectory", type + "." + method);
                Assert.False(type == "System.Reflection.Assembly" && method == "get_Location", type + "." + method);
                Assert.False(type == "System.IO.FileInfo" || type == "System.IO.DirectoryInfo", type + "." + method);
                Assert.False(type == "System.IO.FileStream" || type == "System.IO.StreamReader" || type == "System.IO.StreamWriter", type + "." + method);
            }
        }
    }
}
