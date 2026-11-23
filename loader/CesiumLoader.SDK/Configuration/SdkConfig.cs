using System;
using System.IO;

namespace CesiumLoader.SDK.Configuration
{
    /// <summary>
    /// 旧版 mod 配置 API：原先把每个 mod 的配置集中放在
    /// <c>&lt;游戏目录&gt;/AstralParty_ModLoader/configs/{modName}.json</c>。
    ///
    /// <para>
    /// <b>4.0.0 起配置统一在模组目录：<c>mods/{modName}/config.json</c>。</b>
    /// 本类保留同名 API 只是为了不破坏已编译的第三方 mod ——
    /// <see cref="ConfigDirectory"/> / <see cref="ConfigPath"/> / <see cref="Load{T}"/> /
    /// <see cref="Save{T}"/> / <see cref="Exists"/> 现在全部指向模组目录，
    /// 既不会再读也不会再写 <c>configs</c>（加载器启动时把旧目录改名 <c>configs.bak</c>）。
    /// </para>
    ///
    /// 新代码请用 <see cref="ModConfig"/>：支持热重载（<see cref="ModConfig.PollAllChanged"/>）、
    /// <c>_comments</c> 多语言说明与合并式补默认值。
    ///
    /// 配置对象约定(手写 JSON, 零外部依赖):
    ///  - 公开字段(public field)才会被读写; 属性(property)忽略
    ///  - 支持类型: string / int / long / float / double / bool / 及它们的数组
    ///  - 示例: public class MyConfig { public bool Enabled = true; public int DelayMs = 100; }
    /// </summary>
    [Obsolete("4.0.0 起配置在模组目录: 改用 ModConfig(mods/{ModId}/config.json)。")]
    public static class SdkConfig
    {
        /// <summary>
        /// 模组总目录（<c>&lt;游戏目录&gt;/AstralParty_ModLoader/mods</c>）。
        /// 4.0.0 起每个 mod 的配置在自己的子目录里，用 <see cref="ConfigPath"/> 取具体路径。
        /// </summary>
        public static string ConfigDirectory
        {
            get
            {
                string root = CesiumLoader.SDK.IO.Path.ModsDirectory;
                try { Directory.CreateDirectory(root); } catch { }
                return root;
            }
        }

        /// <summary>mod 配置文件的完整路径（<c>mods/{modName}/config.json</c>；非法文件名字符会被替换为下划线）。</summary>
        public static string ConfigPath(string modName)
        {
            if (string.IsNullOrWhiteSpace(modName)) throw new ArgumentException("modName 不能为空", nameof(modName));
            return System.IO.Path.Combine(CesiumLoader.SDK.IO.Path.GetModDirectory(modName), "config.json");
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

        /// <summary>保存配置(写 JSON, 创建模组目录)。失败时静默返回 false。</summary>
        public static bool Save<T>(string modName, T value)
        {
            try
            {
                if (value == null) return false;
                var path = ConfigPath(modName);
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
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
