using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace HandViewerMod.Tests
{
    public class CompatibilityTests
    {
        [Fact]
        public void CardArtworkLooksUpIdsInDictionaryNotRepeatedList()
        {
            var root = typeof(CompatibilityTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "ModSourceRoot").Value;
            var source = File.ReadAllText(Path.Combine(root, "HandViewerUi.cs"));
            Assert.DoesNotContain("StaticConfigure.Card.Infos[", source);
            Assert.Contains("StaticConfigure.Card.InfoDict[data.cardId]", source);
        }

        [Fact]
        public void ModSourcesAvoidKnownMissingHybridClrBcl()
        {
            var root = typeof(CompatibilityTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "ModSourceRoot").Value;
            var files = Directory.GetFiles(root, "*.cs");
            Assert.True(files.Length >= 4);
            var forbidden = new[] { "DllImport", "new FileStream", "new FileInfo", "new DirectoryInfo",
                "new StreamReader", "new StreamWriter", "Flush(true)", "Flush(false)", "File.ReadAllLines",
                "AppDomain", "Assembly.Location", "FromUnixTime", "Stopwatch" };
            foreach (var file in files)
                foreach (var pattern in forbidden) Assert.DoesNotContain(pattern, File.ReadAllText(file));
        }
    }
}
