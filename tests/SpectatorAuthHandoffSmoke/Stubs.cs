using System;
namespace CesiumLoader.SDK.Mods
{
    public abstract class ModBase
    {
        public virtual string Version { get { return ""; } }
        public virtual void OnInitialize() { }
        public virtual void OnUpdate() { }
        public virtual void OnUnload() { }
        public static void Run(ModBase mod, int delay) { if (delay != 30000) throw new Exception("startup delay"); }
    }
}
namespace CesiumLoader.SDK.Manifests
{
    [Flags] public enum ModPermission { ReadGameState = 1, FileSystem = 256 }
    [AttributeUsage(AttributeTargets.Assembly)] public sealed class ModManifestAttribute : Attribute
    {
        public ModManifestAttribute(string name, string version, string author) { }
        public ModPermission Permissions { get; set; }
        public string SdkVersion { get; set; }
    }
}
namespace party.protocol
{
    public enum AuthType { Dev, China, Other }
    public sealed class ChinaInfo { }
    public sealed class ConnectC2S { public AuthType Auth; public ChinaInfo China; }
}
namespace GameLogic { public static class LoginServiceHelper { public static party.protocol.ConnectC2S connectToken; } }
namespace Core { public static class GameSettings { public static string IP = "localhost"; public static int Port = 1234; public static string APP_VERSION = "synthetic"; public static int VER1=3,VER2=2,VER3=1; } }
namespace Google.Protobuf
{
    public static class MessageExtensions
    {
        public static int Calls;
        public static byte[] ToByteArray(party.protocol.ConnectC2S token) { Calls++; return new byte[] { 1, 2, 3 }; }
    }
}
