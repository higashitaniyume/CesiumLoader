using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.Manifests
{
    /// <summary>一条依赖问题。</summary>
    public sealed class ModDependencyIssue
    {
        public ModDependencyIssue(string modId, ModDependencyIssueKind kind, string detail)
        {
            ModId = modId;
            Kind = kind;
            Detail = detail;
        }

        /// <summary>出问题的 mod。</summary>
        public string ModId { get; private set; }

        /// <summary>问题类别。</summary>
        public ModDependencyIssueKind Kind { get; private set; }

        /// <summary>面向人的说明。</summary>
        public string Detail { get; private set; }

        public override string ToString() { return ModId + ": " + Kind + " (" + Detail + ")"; }
    }
}
