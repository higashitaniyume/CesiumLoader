using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.Manifests
{
    /// <summary>依赖问题类别。</summary>
    public enum ModDependencyIssueKind
    {
        /// <summary>依赖的 mod 不在本次集合里。</summary>
        MissingDependency,

        /// <summary>依赖的 mod 存在, 但版本低于 minVersion。</summary>
        VersionTooLow,

        /// <summary>mod 要求的 SDK 版本高于当前 SDK。</summary>
        SdkVersionTooNew,

        /// <summary>依赖的 mod 自身已被拒绝(缺失/版本不符/循环), 导致本 mod 无法排序。</summary>
        DependencyRejected,

        /// <summary>循环依赖。</summary>
        CircularDependency,

        /// <summary>id 为空。</summary>
        InvalidId,

        /// <summary>id 重复。</summary>
        DuplicateId,
    }
}
