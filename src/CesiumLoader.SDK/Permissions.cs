using System;
using System.Reflection;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// mod 权限(历史遗留, 已取消门控)。
    ///
    /// 权限机制已取消: 任何 mod 调用任何 SDK API 都不再受权限检查,
    /// 敏感操作(如 GameActions 模拟操作)不再默认拒绝、无需声明或覆盖配置。
    ///
    /// 保留内容:
    ///   - ModPermission 枚举: mod 可在 [ModManifest(Permissions=...)] 里声明
    ///     自己会用到哪些能力, 供工具/加载器向用户展示"此 mod 可操作游戏"警告,
    ///     仅提示不阻止。
    ///   - Has()/Require(): 恒返回 true, 保留签名供现有调用点编译,
    ///     不再执行任何检查。
    ///
    /// 已删除: *.permissions.json 覆盖加载、默认策略、敏感位判定 —— 全部取消。
    /// </summary>
    public static class Permissions
    {
        /// <summary>恒返回 true: 权限机制已取消, 不再检查。</summary>
        public static bool Has(ModPermission perm, Assembly caller = null) => true;

        /// <summary>恒返回 true: 权限机制已取消, 不再检查。</summary>
        public static bool Require(ModPermission perm, string apiName, Assembly caller = null) => true;

        /// <summary>程序集安全取名的辅助(解释器下 GetName 可能抛)。</summary>
        internal static string SafeName(Assembly asm)
        {
            if (asm == null) return null;
            try { return asm.GetName()?.Name; } catch { return null; }
        }
    }
}
