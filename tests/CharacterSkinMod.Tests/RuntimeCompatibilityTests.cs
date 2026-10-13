using System.Runtime.CompilerServices;
using Xunit;

namespace CharacterSkinMod.Tests;
public sealed class RuntimeCompatibilityTests
{
    private static string RuntimeSource([CallerFilePath] string caller = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../../mods/CharacterSkinMod/ModEntry.cs"));
    [Fact] public void RuntimeAvoidsKnownUnavailableHybridClrFileApis()
    {
        string source=File.ReadAllText(RuntimeSource());
        foreach(string forbidden in new[]{ "new FileInfo", "new DirectoryInfo", "FileStream", "StreamReader", "StreamWriter", "File.ReadAllLines", "AppDomain.CurrentDomain.BaseDirectory", "Assembly.Location", ".Flush(" })
            Assert.DoesNotContain(forbidden,source,StringComparison.Ordinal);
    }
}
