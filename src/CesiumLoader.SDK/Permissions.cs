using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// mod 权限门控: 敏感 API (GameActions / SpeedHack) 默认关闭,
    /// mod 声明请求 + 运行时检查 + 配置可逐项覆盖。
    ///
    /// 判定顺序 (从宽到严):
    ///   1. mods\{name}.permissions.json 显式配置 (最高优先级, 管理员/用户可强制开/关)
    ///   2. [ModManifest(Permissions=...)] 声明 (mod 请求)
    ///   3. 默认: ReadGameState / FileWrite 授予; GameActions / SpeedHack 拒绝
    ///
    /// 敏感 API 实现侧调用 Permission.Require(...) 检查; 未授权时静默降级
    /// (返回 false / 不执行), 并输出一条 SdkLog.Warn, 不抛异常。
    /// </summary>
    public static class Permissions
    {
        private static readonly Dictionary<string, ModPermission> _overrides =
            new Dictionary<string, ModPermission>(StringComparer.OrdinalIgnoreCase);
        private static bool _overridesLoaded;

        /// <summary>加载权限覆盖配置(mods\{name}.permissions.json)。幂等。</summary>
        public static void LoadOverrides(string modsDir)
        {
            try
            {
                if (_overridesLoaded) return;
                _overridesLoaded = true;
                if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return;

                foreach (var f in Directory.GetFiles(modsDir, "*.permissions.json"))
                {
                    try
                    {
                        // 格式: {"modName": {"GameActions": true, "SpeedHack": false}, ...}
                        var text = File.ReadAllText(f);
                        // 极简 JSON 扫描: 按 "modName": { "perm": bool } 提取
                        var dict = ParsePermissionsJson(text);
                        foreach (var kv in dict) _overrides[kv.Key] = kv.Value;
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 检查调用者 mod 是否拥有指定权限。
        /// </summary>
        /// <param name="perm">要检查的权限位。</param>
        /// <param name="caller">调用者程序集(默认取调用栈)。</param>
        /// <returns>true = 已授权。</returns>
        public static bool Has(ModPermission perm, Assembly caller = null)
        {
            try
            {
                caller = caller ?? SdkInfo.CallingAssembly();
                string modId = SafeName(caller);
                if (string.IsNullOrEmpty(modId)) return false;

                // 1. 显式覆盖配置
                ModPermission overridePerm;
                if (_overrides.TryGetValue(modId, out overridePerm))
                    return (overridePerm & perm) == perm;

                // 2. 声明
                var manifest = SdkInfo.ManifestOf(caller);
                ModPermission declared = manifest.Permissions;

                // 3. 默认策略: 敏感位默认拒绝, 只读/文件位默认授予
                const ModPermission sensitive = ModPermission.GameActions | ModPermission.SpeedHack;
                if ((perm & sensitive) != 0)
                {
                    // 敏感权限: 必须显式声明才授予
                    return (declared & perm) == perm;
                }
                // 非敏感: 声明了按声明, 没声明默认授予
                return declared == ModPermission.None || (declared & perm) == perm;
            }
            catch { return false; }
        }

        /// <summary>
        /// 敏感 API 调用前检查; 未授权时记录警告并返回 false。
        /// 用法: if (!Permissions.Require(ModPermission.GameActions, "GameActions.ThrowDice")) return false;
        /// </summary>
        public static bool Require(ModPermission perm, string apiName, Assembly caller = null)
        {
            bool ok = Has(perm, caller);
            if (!ok)
            {
                try
                {
                    string modId = SafeName(caller ?? SdkInfo.CallingAssembly());
                    SdkLog.Warn("Perm", $"mod '{modId}' 调用敏感 API '{apiName}' 被拒绝: 缺少权限 {perm}。"
                        + $" 请在 [ModManifest(Permissions=...)] 声明, 或用 mods\\{modId}.permissions.json 授予");
                }
                catch { }
            }
            return ok;
        }

        /// <summary>程序集安全取名的辅助(解释器下 GetName 可能抛)。</summary>
        internal static string SafeName(Assembly asm)
        {
            if (asm == null) return null;
            try { return asm.GetName()?.Name; } catch { return null; }
        }

        // ---------- 极简权限 JSON 解析 ----------
        // 格式: {"ModA": {"GameActions": true}, "ModB": {"SpeedHack": false, "GameActions": true}}
        private static Dictionary<string, ModPermission> ParsePermissionsJson(string text)
        {
            var result = new Dictionary<string, ModPermission>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return result;

            int i = 0;
            while (i < text.Length)
            {
                // 找 "modName"
                int q1 = text.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = text.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                string modId = text.Substring(q1 + 1, q2 - q1 - 1);
                i = q2 + 1;

                int brace = text.IndexOf('{', i);
                if (brace < 0) break;
                int closeBrace = text.IndexOf('}', brace);
                if (closeBrace < 0) break;

                string body = text.Substring(brace, closeBrace - brace);
                ModPermission perm = ModPermission.None;
                // 扫描 body 里的 "PermName": true/false
                int j = 0;
                while (j < body.Length)
                {
                    int p1 = body.IndexOf('"', j);
                    if (p1 < 0) break;
                    int p2 = body.IndexOf('"', p1 + 1);
                    if (p2 < 0) break;
                    string permName = body.Substring(p1 + 1, p2 - p1 - 1);
                    j = p2 + 1;
                    int colon = body.IndexOf(':', j);
                    if (colon < 0) break;
                    int t = colon + 1;
                    while (t < body.Length && (body[t] == ' ' || body[t] == '\t' || body[t] == '\r' || body[t] == '\n')) t++;
                    bool val = body.Length >= t + 4 && body.Substring(t, 4) == "true";
                    ModPermission bit;
                    if (Enum.TryParse(permName, true, out bit))
                    {
                        if (val) perm |= bit;
                    }
                    j = t;
                }
                result[modId] = perm;
                i = closeBrace + 1;
            }
            return result;
        }
    }
}
