using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.Manifests
{
    /// <summary>依赖图中的一个 mod 节点(工具/校验用输入)。</summary>
    public sealed class ModDependencyNode
    {
        public ModDependencyNode(string id, string version = null, string sdkVersion = null,
                                 IEnumerable<ModDependency> dependencies = null)
        {
            Id = id;
            Version = version;
            SdkVersion = sdkVersion;
            Dependencies = dependencies == null
                ? new List<ModDependency>()
                : new List<ModDependency>(dependencies);
        }

        /// <summary>mod 标识(程序集名)。</summary>
        public string Id { get; private set; }

        /// <summary>mod 版本(SemVer)。</summary>
        public string Version { get; private set; }

        /// <summary>mod 声明的 SDK 版本要求(SemVer; 空 = 不要求)。</summary>
        public string SdkVersion { get; private set; }

        /// <summary>依赖列表。</summary>
        public List<ModDependency> Dependencies { get; private set; }

        /// <summary>从 [ModManifest] 构造。</summary>
        public static ModDependencyNode FromManifest(ModManifestAttribute manifest)
        {
            if (manifest == null) return null;
            return new ModDependencyNode(manifest.Name, manifest.Version, manifest.SdkVersion, manifest.Dependencies);
        }
    }
}
