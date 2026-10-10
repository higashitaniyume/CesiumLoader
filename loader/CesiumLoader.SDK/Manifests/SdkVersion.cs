using System;
using System.Reflection;
using System.Text;

namespace CesiumLoader.SDK.Manifests
{
    /// <summary>
    /// SDK 版本与兼容信息。mod 通过 [ModManifest(SdkVersion=...)] 声明需要的版本,
    /// 加载器在加载时校验; 运行时可用 VersionAtLeast 做防御式检查。
    /// </summary>
    public static class SdkVersion
    {
        /// <summary>当前 SDK 版本 (SemVer)。与 CesiumLoader.SDK.csproj 的 Version 保持一致。</summary>
        public const string Current = "2.4.0";

        /// <summary>加载器原生层版本(与 version.dll 构建对应)。</summary>
        public const string LoaderVersion = "2.4.0";

        /// <summary>最小可接受的 mod 声明的 SDK 版本。</summary>
        public static bool Accepts(string modSdkVersion)
        {
            if (string.IsNullOrEmpty(modSdkVersion)) return true;   // 未声明 = 兼容
            return Compare(modSdkVersion, Current) <= 0;
        }

        /// <summary>mod 声明的 SDK 版本是否 ≥ 给定版本(运行时防御检查)。</summary>
        public static bool DeclaredAtLeast(Assembly asm, string minVersion)
        {
            try
            {
                var m = SdkInfo.ManifestOf(asm);
                return !string.IsNullOrEmpty(m.SdkVersion) && Compare(m.SdkVersion, minVersion) >= 0;
            }
            catch { return false; }
        }

        /// <summary>SemVer 比较(a 比 b 大返回 &gt;0, 相等 0, 小 &lt;0)。只比较主.次.修订数字。</summary>
        public static int Compare(string a, string b)
        {
            int[] Pa = Parse(a), Pb = Parse(b);
            for (int i = 0; i < 3; i++)
            {
                if (Pa[i] != Pb[i]) return Pa[i] > Pb[i] ? 1 : -1;
            }
            return 0;
        }

        private static int[] Parse(string v)
        {
            var r = new int[3];
            if (string.IsNullOrEmpty(v)) return r;
            var parts = v.Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                int n;
                if (int.TryParse(parts[i].Trim(), out n)) r[i] = n;
            }
            return r;
        }
    }
}
