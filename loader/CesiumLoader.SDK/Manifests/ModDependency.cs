using System;
using System.Reflection;
using System.Text;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Speed;

namespace CesiumLoader.SDK.Manifests
{
    /// <summary>
    /// mod 依赖声明: 依赖的另一个 mod(按程序集名) + 最低版本。
    /// </summary>
    public sealed class ModDependency
    {
        public ModDependency(string id, string minVersion = null)
        {
            Id = id;
            MinVersion = minVersion;
        }

        /// <summary>被依赖 mod 的程序集名(不带 .dll)。</summary>
        public string Id { get; }

        /// <summary>最低版本(SemVer, 可空 = 任意版本)。</summary>
        public string MinVersion { get; }
    }
}
