using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CesiumLoader.SDK.Configuration
{
    /// <summary>
    /// mod 配置: 每个 mod 一个独立 JSON 配置文件, 读写即存。
    /// 路径: &lt;游戏目录&gt;/AstralParty_ModLoader/configs/{modName}.json
    /// (通过 CESIUM_MODS_DIR 定位; 未设置时回退到 %LocalAppData%/AstralParty_ModLoader/configs)。
    ///
    /// 配置对象约定(手写 JSON, 零外部依赖):
    ///  - 公开字段(public field)才会被读写; 属性(property)忽略
    ///  - 支持类型: string / int / long / float / double / bool / 及它们的数组
    ///  - 示例: public class MyConfig { public bool Enabled = true; public int DelayMs = 100; }
    /// </summary>
    public static class SdkConfig
    {
        /// <summary>配置目录(自动创建)。</summary>
        public static string ConfigDirectory
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
                string root;
                if (!string.IsNullOrEmpty(dir))
                {
                    // CESIUM_MODS_DIR 指向 <游戏目录>/AstralParty_ModLoader/mods
                    root = Path.GetFullPath(Path.Combine(dir, "..", "configs"));
                }
                else
                {
                    root = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AstralParty_ModLoader", "configs");
                }
                try { Directory.CreateDirectory(root); } catch { }
                return root;
            }
        }

        /// <summary>mod 配置文件的完整路径(modName 非法字符会被替换为下划线)。</summary>
        public static string ConfigPath(string modName)
        {
            if (string.IsNullOrWhiteSpace(modName)) throw new ArgumentException("modName 不能为空", nameof(modName));
            var safe = string.Concat(modName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(ConfigDirectory, safe + ".json");
        }

        /// <summary>读取配置; 文件不存在或损坏时返回 default(T)。</summary>
        public static T Load<T>(string modName) where T : class, new()
        {
            try
            {
                var path = ConfigPath(modName);
                if (!File.Exists(path)) return new T();
                var json = File.ReadAllText(path);
                return MiniJson.ParseObject<T>(json);
            }
            catch { return new T(); }
        }

        /// <summary>保存配置(写 JSON, 创建目录)。失败时静默返回 false。</summary>
        public static bool Save<T>(string modName, T value)
        {
            try
            {
                if (value == null) return false;
                var path = ConfigPath(modName);
                var json = MiniJson.SerializeObject(value);
                File.WriteAllText(path, json);
                return true;
            }
            catch { return false; }
        }

        /// <summary>配置是否存在。</summary>
        public static bool Exists(string modName)
        {
            try { return File.Exists(ConfigPath(modName)); }
            catch { return false; }
        }
    }
}
