using System;
using System.Reflection;
using System.Text;

namespace CesiumLoader.SDK.Manifests
{
    public sealed class ModManifestAttribute : Attribute
    {
        public ModManifestAttribute(string name, string version = "1.0.0", string author = "", string description = "")
        {
            Name = name;
            Version = version;
            Author = author;
            Description = description;
        }

        public string Name { get; }
        public string Version { get; }
        public string Author { get; }
        public string Description { get; }

        /// <summary>mod 请求的权限(默认 None; ReadGameState/FileWrite 默认授予, 敏感项默认拒绝)。</summary>
        public ModPermission Permissions { get; set; } = ModPermission.None;

        /// <summary>依赖的 SDK 最低版本(SemVer, 如 "2.2.1")。加载器会检查兼容性。</summary>
        public string SdkVersion { get; set; }

        /// <summary>依赖的其他 mod(程序集名 + 最低版本)。加载器按依赖顺序加载。</summary>
        public ModDependency[] Dependencies { get; set; } = Array.Empty<ModDependency>();
    }
}
