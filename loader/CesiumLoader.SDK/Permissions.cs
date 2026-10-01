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

        // =====================================================================
        // 展示 / 审计辅助 —— 权限不再门控, 但仍然要让用户看得见 mod 会动到什么
        // =====================================================================

        /// <summary>已定义的权限位(按位从低到高)。</summary>
        private static readonly ModPermission[] Known =
        {
            ModPermission.ReadGameState,
            ModPermission.GameActions,
            ModPermission.SpeedHack,
            ModPermission.FileWrite,
            ModPermission.ModifyGameState,
            ModPermission.Camera,
            ModPermission.Input,
            ModPermission.UI,
            ModPermission.FileSystem,
            ModPermission.Network,
            ModPermission.Debug,
        };

        /// <summary>遍历所有已定义权限位。</summary>
        public static ModPermission[] All()
        {
            var copy = new ModPermission[Known.Length];
            Array.Copy(Known, copy, Known.Length);
            return copy;
        }

        /// <summary>展开为已声明权限的名称列表(位序稳定, 未知位忽略)。</summary>
        public static string[] Names(ModPermission permissions)
        {
            var list = new System.Collections.Generic.List<string>(4);
            for (int i = 0; i < Known.Length; i++)
            {
                if ((permissions & Known[i]) == Known[i]) list.Add(Known[i].ToString());
            }
            return list.ToArray();
        }

        /// <summary>可读描述(工具输出 / 启动横幅 / 审计日志用)。</summary>
        public static string Describe(ModPermission permissions)
        {
            var names = Names(permissions);
            return names.Length == 0 ? "None" : string.Join(", ", names);
        }

        /// <summary>
        /// 该权限是否属于"会改变游戏或对外产生副作用"的敏感类别 ——
        /// 仅用于在工具/UI 里高亮提示, <b>不</b>用于阻止调用。
        /// </summary>
        public static bool IsSensitive(ModPermission perm)
        {
            return (perm & (ModPermission.GameActions |
                            ModPermission.SpeedHack |
                            ModPermission.ModifyGameState |
                            ModPermission.Network)) != 0;
        }
    }
}
