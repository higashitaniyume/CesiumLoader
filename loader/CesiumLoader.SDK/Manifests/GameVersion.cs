namespace CesiumLoader.SDK.Manifests
{
    /// <summary>
    /// 记录 SDK 编译时对应的游戏热更程序集身份, 供<b>打包期</b>校验。
    ///
    /// 为什么运行期不比对: 这个游戏是 IL2CPP + HybridCLR, 热更环境里的 BCL 被裁剪过 ——
    /// <c>System.Reflection.Assembly.ManifestModule</c> 这类成员在热更程序集里根本不存在,
    /// 一旦调用就是 <b>JIT 期</b>抛 MissingMethodException(try/catch 拦不住, 异常直接从
    /// 方法的调用点冒出去)。3.7.2 就是这么把 23 个模组的入口一起炸掉的
    /// (<c>entry failed: run_entry: MissingMethodException: MethodNotFind
    /// System.Reflection.Assembly::get_ManifestModule</c>)。
    ///
    /// 所以运行期一律不拦, 改由这三层兜底:
    ///   1. <b>回调护栏</b>(<c>ModCallbackGuard</c>): 模组自己的回调抛异常不会逃进游戏的
    ///      RPC/UniTask 链, 最多是那个功能失效, 不会把整局卡死;
    ///   2. <b>打包期校验</b>(<c>tools/stage-modloader-dist.ps1</c>): 换了游戏程序集却忘了
    ///      同步这里的标记, 直接拒绝出包;
    ///   3. <b>每次游戏热更都重编译全部模组</b>: 模组必须对着当前的游戏程序集编译。
    ///
    /// 维护: 换 <c>refs/game/AstralParty.Runtime.dll</c> 时必须同步更新
    /// <see cref="CompiledGameAssemblyMvid"/>(打包脚本与单元测试都会校验)。
    /// </summary>
    public static class GameVersion
    {
        /// <summary>编译时 <c>refs/game/AstralParty.Runtime.dll</c> 的 MVID(小写, 带连字符)。</summary>
        public const string CompiledGameAssemblyMvid = "590fc02c-b32c-4f19-bd0d-9057778e9825";
    }
}
