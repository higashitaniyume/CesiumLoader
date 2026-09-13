using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// manifest 旁车文件(sidecar): 把 mod 的 [ModManifest] 元数据导出成同名 .json
    /// (ActivityLogMod.dll → ActivityLogMod.json), 供外部工具(如 apt 模组列表)读取显示,
    /// 不需要加载 mod 程序集。
    ///
    /// 注意: 热更程序集由 Assembly.Load(byte[]) 加载, Assembly.Location 在 HybridCLR
    /// 解释器下会抛 MissingMethodException —— 因此不依赖 Location, 而是写到
    /// CESIUM_MODS_DIR 下以程序集名命名的 mod 文件夹里
    /// (mods\{modName}\{modName}.json, 与 DLL 同文件夹)。
    /// </summary>
    public static class SdkManifest
    {
        /// <summary>把调用者程序集的 [ModManifest] 写成 mods 目录下该 mod 文件夹里的同名 .json
        /// (mods\{modName}\{modName}.json)。幂等, 失败静默。</summary>
        public static void ExportSidecar()
        {
            try
            {
                var asm = SdkInfo.CallingAssembly();
                if (asm == null) return;
                var manifest = SdkInfo.ManifestOf(asm);

                // 程序集名(GetName 在解释器下安全; 失败则放弃)
                string modName = null;
                try { modName = asm.GetName()?.Name; } catch { }
                if (string.IsNullOrEmpty(modName)) return;

                string modsDir = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
                if (string.IsNullOrEmpty(modsDir)) return;
                Directory.CreateDirectory(modsDir);

                // 新布局: 每 mod 一个文件夹 mods\{modName}\{modName}.json (sidecar 与 DLL 同文件夹)
                var modFolder = Path.Combine(modsDir, modName);
                Directory.CreateDirectory(modFolder);
                var sidecar = Path.Combine(modFolder, modName + ".json");
                File.WriteAllText(sidecar, Serialize(manifest, modName));
            }
            catch { }
        }

        /// <summary>把 ModManifest 转成 JSON(与外部读取器同一格式)。
        /// "id" 是程序集名(依赖解析用它), "name" 是显示名。</summary>
        public static string Serialize(ModManifestAttribute m, string assemblyName = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonString(assemblyName ?? m.Name));
            sb.Append(",\"name\":").Append(JsonString(m.Name));
            sb.Append(",\"version\":").Append(JsonString(m.Version));
            sb.Append(",\"author\":").Append(JsonString(m.Author));
            sb.Append(",\"description\":").Append(JsonString(m.Description));
            sb.Append(",\"permissions\":").Append((int)m.Permissions);
            sb.Append(",\"sdkVersion\":").Append(JsonString(m.SdkVersion));
            // 依赖
            sb.Append(",\"dependencies\":[");
            if (m.Dependencies != null)
            {
                bool first = true;
                foreach (var d in m.Dependencies)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":").Append(JsonString(d.Id));
                    sb.Append(",\"minVersion\":").Append(JsonString(d.MinVersion)).Append('}');
                }
            }
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        private static string JsonString(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (var c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
