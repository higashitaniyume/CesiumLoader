using System;
using System.Threading;

namespace CesiumLoader.SDK.Events
{
    /// <summary>场景加载完成参数。</summary>
    public struct SceneLoadedEventArgs
    {
        /// <summary>场景名字。</summary>
        public string Name;

        /// <summary>场景路径。</summary>
        public string Path;

        /// <summary>构建索引(-1 表示不在 Build Settings 中)。</summary>
        public int BuildIndex;

        /// <summary>加载模式(0=Single, 1=Additive)。</summary>
        public int LoadMode;

        /// <summary>可读文本。</summary>
        public override string ToString()
        {
            return (string.IsNullOrEmpty(Name) ? "(未命名)" : Name) + " mode=" + LoadMode + " index=" + BuildIndex;
        }
    }
}
