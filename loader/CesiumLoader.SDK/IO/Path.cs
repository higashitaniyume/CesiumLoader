using System;
using System.IO;
using CesiumLoader.SDK.Mods;

namespace CesiumLoader.SDK.IO
{
    /// <summary>
    /// 提供 CesiumLoader 游戏、加载器与模组各级路径发现、定位及标准路径拼接 API。
    /// 包装了常用路径操作，避免模组中手动拼凑路径或依赖不稳定的环境推导。
    /// </summary>
    public static class Path
    {
        // =====================================================================
        // 分隔符与常量
        // =====================================================================

        /// <summary>目录分隔符（Windows 下为 '\'）。</summary>
        public static char DirectorySeparatorChar { get { return System.IO.Path.DirectorySeparatorChar; } }

        /// <summary>备用目录分隔符（'/'）。</summary>
        public static char AltDirectorySeparatorChar { get { return System.IO.Path.AltDirectorySeparatorChar; } }

        /// <summary>路径分隔符（';'）。</summary>
        public static char PathSeparator { get { return System.IO.Path.PathSeparator; } }

        /// <summary>卷分隔符（':'）。</summary>
        public static char VolumeSeparatorChar { get { return System.IO.Path.VolumeSeparatorChar; } }

        // =====================================================================
        // 核心目录（找路径）
        // =====================================================================

        /// <summary>
        /// 加载器根目录（例如：&lt;游戏目录&gt;\AstralParty_ModLoader）。
        /// 优先从 CESIUM_LOADER_DIR、CESIUM_MODS_DIR、CESIUM_LOG_DIR 等环境变量向上推导，
        /// 未设置时回退至 %LocalAppData%\AstralParty_ModLoader。
        /// </summary>
        public static string LoaderDirectory
        {
            get
            {
                string loaderEnv = EnvOrNull("CESIUM_LOADER_DIR");
                if (!string.IsNullOrEmpty(loaderEnv)) return loaderEnv;

                string modsDir = EnvOrNull("CESIUM_MODS_DIR");
                if (!string.IsNullOrEmpty(modsDir))
                {
                    string parent = System.IO.Path.GetDirectoryName(modsDir.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parent)) return parent;
                }

                string logsDir = EnvOrNull("CESIUM_LOG_DIR");
                if (!string.IsNullOrEmpty(logsDir))
                {
                    string parent = System.IO.Path.GetDirectoryName(logsDir.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parent)) return parent;
                }

                string sdkDir = EnvOrNull("CESIUM_SDK_DIR");
                if (!string.IsNullOrEmpty(sdkDir))
                {
                    string parent = System.IO.Path.GetDirectoryName(sdkDir.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parent)) return parent;
                }

                // 若能探测到游戏根目录且存在 AstralParty_ModLoader 目录
                string gameDir = TryGetGameDirFromUnityOrProcess();
                if (!string.IsNullOrEmpty(gameDir))
                {
                    string candidate = System.IO.Path.Combine(gameDir, "AstralParty_ModLoader");
                    if (System.IO.Directory.Exists(candidate)) return candidate;
                }

                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return System.IO.Path.Combine(localApp, "AstralParty_ModLoader");
            }
        }

        /// <summary><see cref="LoaderDirectory"/> 的别名。</summary>
        public static string LoaderRootDirectory { get { return LoaderDirectory; } }

        /// <summary>
        /// 游戏可执行文件所在根目录（包含 AstralParty_CN.exe 的目录）。
        /// 优先读取 CESIUM_GAME_DIR，或从 LoaderDirectory 向上推导，或从 Unity dataPath 向上推导。
        /// </summary>
        public static string GameDirectory
        {
            get
            {
                string gameEnv = EnvOrNull("CESIUM_GAME_DIR");
                if (!string.IsNullOrEmpty(gameEnv)) return gameEnv;

                string loaderDir = LoaderDirectory;
                if (!string.IsNullOrEmpty(loaderDir))
                {
                    string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    // 仅当 loaderDir 不在 LocalAppData 下时，其父目录为游戏根目录
                    if (!loaderDir.StartsWith(localApp, StringComparison.OrdinalIgnoreCase))
                    {
                        string parent = System.IO.Path.GetDirectoryName(loaderDir.TrimEnd('\\', '/'));
                        if (!string.IsNullOrEmpty(parent)) return parent;
                    }
                }

                string unityGameDir = TryGetGameDirFromUnityOrProcess();
                if (!string.IsNullOrEmpty(unityGameDir)) return unityGameDir;

                return Environment.CurrentDirectory;
            }
        }

        /// <summary><see cref="GameDirectory"/> 的别名。</summary>
        public static string GameRootDirectory { get { return GameDirectory; } }

        /// <summary>模组总目录（&lt;加载器目录&gt;\mods）。</summary>
        public static string ModsDirectory
        {
            get
            {
                string env = EnvOrNull("CESIUM_MODS_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(LoaderDirectory, "mods");
            }
        }

        /// <summary>模组配置文件目录（&lt;加载器目录&gt;\configs）。</summary>
        public static string ConfigsDirectory
        {
            get
            {
                return System.IO.Path.Combine(LoaderDirectory, "configs");
            }
        }

        /// <summary>日志目录（&lt;加载器目录&gt;\logs）。</summary>
        public static string LogsDirectory
        {
            get
            {
                string env = EnvOrNull("CESIUM_LOG_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(LoaderDirectory, "logs");
            }
        }

        /// <summary>皮肤素材目录（&lt;加载器目录&gt;\skins）。</summary>
        public static string SkinsDirectory
        {
            get
            {
                return System.IO.Path.Combine(LoaderDirectory, "skins");
            }
        }

        /// <summary>音频子系统通信目录（&lt;加载器目录&gt;\audio）。</summary>
        public static string AudioDirectory
        {
            get
            {
                string env = EnvOrNull("CESIUM_AUDIO_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(LoaderDirectory, "audio");
            }
        }

        /// <summary>音效方案根目录（&lt;加载器目录&gt;\audio-schemes）。</summary>
        public static string AudioSchemesDirectory
        {
            get
            {
                return System.IO.Path.Combine(LoaderDirectory, "audio-schemes");
            }
        }

        /// <summary>变速控制通信目录（&lt;加载器目录&gt;\speed）。</summary>
        public static string SpeedDirectory
        {
            get
            {
                string env = EnvOrNull("CESIUM_SPEED_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(LoaderDirectory, "speed");
            }
        }

        /// <summary>窗口焦点通信目录（&lt;加载器目录&gt;\window）。</summary>
        public static string WindowDirectory
        {
            get
            {
                string env = EnvOrNull("CESIUM_WINDOW_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(LoaderDirectory, "window");
            }
        }

        /// <summary>SDK 程序集目录（&lt;加载器目录&gt;\sdk）。</summary>
        public static string SdkDirectory
        {
            get
            {
                string env = EnvOrNull("CESIUM_SDK_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(LoaderDirectory, "sdk");
            }
        }

        /// <summary>引导程序目录（&lt;加载器目录&gt;\bootstrap）。</summary>
        public static string BootstrapDirectory
        {
            get
            {
                string env = EnvOrNull("CESIUM_BOOTSTRAP_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(LoaderDirectory, "bootstrap");
            }
        }

        /// <summary>
        /// 当前正在执行的模组目录（基于 <see cref="ModContext.Current"/>）。
        /// 若在 Mod 上下文外调用，则回退为 <see cref="ModsDirectory"/>。
        /// </summary>
        public static string CurrentModDirectory
        {
            get
            {
                try
                {
                    string dir = ModContext.Current != null ? ModContext.Current.Directory : null;
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
                catch { }
                return ModsDirectory;
            }
        }

        // =====================================================================
        // 会话日志文件路径
        // =====================================================================

        /// <summary>当前会话的加载器引导日志文件完整路径（CESIUM_LOG_FILE）。</summary>
        public static string CurrentLogFile
        {
            get
            {
                string env = EnvOrNull("CESIUM_LOG_FILE");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(LogsDirectory, "cesium-loader.log");
            }
        }

        /// <summary>当前会话的活动日志文件完整路径（CESIUM_ACTIVITY_LOG_FILE）。</summary>
        public static string CurrentActivityLogFile
        {
            get
            {
                string env = EnvOrNull("CESIUM_ACTIVITY_LOG_FILE");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(LogsDirectory, "activity-mod.log");
            }
        }

        /// <summary>当前会话的模组错误日志文件完整路径（CESIUM_ERROR_LOG_FILE）。</summary>
        public static string CurrentErrorLogFile
        {
            get
            {
                string env = EnvOrNull("CESIUM_ERROR_LOG_FILE");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(LogsDirectory, "mod-errors.log");
            }
        }

        // =====================================================================
        // 便捷定位与子路径拼接 API（免得自己拼路径）
        // =====================================================================

        /// <summary>获取游戏根目录下的子路径。</summary>
        public static string GetGamePath(params string[] subPaths)
        {
            return CombineInternal(GameDirectory, subPaths);
        }

        /// <summary>获取加载器根目录下的子路径。</summary>
        public static string GetLoaderPath(params string[] subPaths)
        {
            return CombineInternal(LoaderDirectory, subPaths);
        }

        /// <summary>获取指定模组的根目录（&lt;ModsDirectory&gt;\{modId}）。</summary>
        public static string GetModDirectory(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return ModsDirectory;
            string safe = SanitizeFileName(modId);
            return System.IO.Path.Combine(ModsDirectory, safe);
        }

        /// <summary>获取指定模组目录下的子路径（&lt;ModsDirectory&gt;\{modId}\...）。</summary>
        public static string GetModPath(string modId, params string[] subPaths)
        {
            return CombineInternal(GetModDirectory(modId), subPaths);
        }

        /// <summary>获取当前执行模组目录下的子路径。</summary>
        public static string GetModPath(params string[] subPaths)
        {
            return CombineInternal(CurrentModDirectory, subPaths);
        }

        /// <summary>获取指定模组的数据目录（&lt;ModsDirectory&gt;\{modId}\data）。</summary>
        public static string GetModDataDirectory(string modId)
        {
            return GetModPath(modId, "data");
        }

        /// <summary>获取指定模组数据目录下的子路径（&lt;ModsDirectory&gt;\{modId}\data\...）。</summary>
        public static string GetModDataPath(string modId, params string[] subPaths)
        {
            return CombineInternal(GetModDataDirectory(modId), subPaths);
        }

        /// <summary>
        /// 解析指定模组的配置文件路径。
        /// 优先查找 mods/{modId}/config.json，其次查找 configs/{modId}.json。
        /// </summary>
        public static string GetModConfigPath(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return null;
            string safe = SanitizeFileName(modId);

            string inModDir = System.IO.Path.Combine(GetModDirectory(safe), "config.json");
            try
            {
                if (System.IO.File.Exists(inModDir)) return inModDir;
            }
            catch { }

            string inConfigDir = System.IO.Path.Combine(ConfigsDirectory, safe + ".json");
            try
            {
                if (System.IO.File.Exists(inConfigDir)) return inConfigDir;
            }
            catch { }

            try
            {
                if (System.IO.Directory.Exists(GetModDirectory(safe))) return inModDir;
            }
            catch { }

            return inConfigDir;
        }

        /// <summary>获取 configs 目录下的配置文件路径（若无 .json 后缀会自动追加）。</summary>
        public static string GetConfigPath(string fileNameOrModId)
        {
            if (string.IsNullOrEmpty(fileNameOrModId)) return ConfigsDirectory;
            string safe = SanitizeFileName(fileNameOrModId);
            if (!safe.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                safe += ".json";
            }
            return System.IO.Path.Combine(ConfigsDirectory, safe);
        }

        /// <summary>获取日志目录下的子路径。</summary>
        public static string GetLogPath(params string[] subPaths)
        {
            return CombineInternal(LogsDirectory, subPaths);
        }

        /// <summary>获取指定皮肤名称的目录（&lt;SkinsDirectory&gt;\{skinName}）。</summary>
        public static string GetSkinDirectory(string skinName)
        {
            if (string.IsNullOrEmpty(skinName)) return SkinsDirectory;
            string safe = SanitizeFileName(skinName);
            return System.IO.Path.Combine(SkinsDirectory, safe);
        }

        /// <summary>获取指定皮肤目录下的子路径。</summary>
        public static string GetSkinPath(string skinName, params string[] subPaths)
        {
            return CombineInternal(GetSkinDirectory(skinName), subPaths);
        }

        /// <summary>获取音频目录下的子路径。</summary>
        public static string GetAudioPath(params string[] subPaths)
        {
            return CombineInternal(AudioDirectory, subPaths);
        }

        /// <summary>获取指定音效方案名称的目录（&lt;AudioSchemesDirectory&gt;\{schemeId}）。</summary>
        public static string GetAudioSchemeDirectory(string schemeId)
        {
            if (string.IsNullOrEmpty(schemeId)) return AudioSchemesDirectory;
            string safe = SanitizeFileName(schemeId);
            return System.IO.Path.Combine(AudioSchemesDirectory, safe);
        }

        /// <summary>获取指定音效方案目录下的子路径。</summary>
        public static string GetAudioSchemePath(string schemeId, params string[] subPaths)
        {
            return CombineInternal(GetAudioSchemeDirectory(schemeId), subPaths);
        }

        // =====================================================================
        // 路径辅助与安全处理 API
        // =====================================================================

        /// <summary>安全确保目录存在（不存在时自动递归创建），并返回该目录路径。</summary>
        public static string EnsureDirectoryExists(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath)) return directoryPath;
            try
            {
                if (!System.IO.Directory.Exists(directoryPath))
                {
                    System.IO.Directory.CreateDirectory(directoryPath);
                }
            }
            catch { }
            return directoryPath;
        }

        /// <summary>将文件名中的非法字符替换为指定字符（默认下划线 '_'）。</summary>
        public static string SanitizeFileName(string fileName, char replacement = '_')
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            char[] chars = fileName.ToCharArray();
            bool modified = false;
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j])
                    {
                        chars[i] = replacement;
                        modified = true;
                        break;
                    }
                }
            }
            return modified ? new string(chars) : fileName;
        }

        /// <summary>标准化路径格式：统一斜杠方向并移除尾部分隔符。</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string p = path.Replace('/', '\\');
            return p.Length > 1 ? p.TrimEnd('\\') : p;
        }

        // =====================================================================
        // System.IO.Path 常用操作包装（免除模组开发者同时引用 System.IO 的繁琐）
        // =====================================================================

        /// <summary>连接两个路径字符串。</summary>
        public static string Combine(string path1, string path2)
        {
            return System.IO.Path.Combine(path1 ?? string.Empty, path2 ?? string.Empty);
        }

        /// <summary>连接三个路径字符串。</summary>
        public static string Combine(string path1, string path2, string path3)
        {
            return System.IO.Path.Combine(path1 ?? string.Empty, path2 ?? string.Empty, path3 ?? string.Empty);
        }

        /// <summary>连接四个路径字符串。</summary>
        public static string Combine(string path1, string path2, string path3, string path4)
        {
            return System.IO.Path.Combine(path1 ?? string.Empty, path2 ?? string.Empty, path3 ?? string.Empty, path4 ?? string.Empty);
        }

        /// <summary>连接路径字符串数组。</summary>
        public static string Combine(params string[] paths)
        {
            if (paths == null || paths.Length == 0) return string.Empty;
            return System.IO.Path.Combine(paths);
        }

        /// <summary>返回指定路径字符串的目录信息。</summary>
        public static string GetDirectoryName(string path)
        {
            return System.IO.Path.GetDirectoryName(path);
        }

        /// <summary>返回指定路径字符串的文件名和扩展名。</summary>
        public static string GetFileName(string path)
        {
            return System.IO.Path.GetFileName(path);
        }

        /// <summary>返回不具有扩展名的指定路径字符串的文件名。</summary>
        public static string GetFileNameWithoutExtension(string path)
        {
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>返回指定路径字符串的扩展名（包含句点“.”）。</summary>
        public static string GetExtension(string path)
        {
            return System.IO.Path.GetExtension(path);
        }

        /// <summary>更改路径字符串的扩展名。</summary>
        public static string ChangeExtension(string path, string extension)
        {
            return System.IO.Path.ChangeExtension(path, extension);
        }

        /// <summary>返回指定路径字符串的绝对路径。</summary>
        public static string GetFullPath(string path)
        {
            return System.IO.Path.GetFullPath(path);
        }

        /// <summary>确定路径是否包括文件扩展名。</summary>
        public static bool HasExtension(string path)
        {
            return System.IO.Path.HasExtension(path);
        }

        /// <summary>获取指定路径的根目录信息。</summary>
        public static string GetPathRoot(string path)
        {
            return System.IO.Path.GetPathRoot(path);
        }

        /// <summary>获取一个值，该值指示指定的路径字符串是包含绝对路径信息还是包含相对路径信息。</summary>
        public static bool IsPathRooted(string path)
        {
            return System.IO.Path.IsPathRooted(path);
        }

        /// <summary>获取包含不能在文件名中指定的字符的数组。</summary>
        public static char[] GetInvalidFileNameChars()
        {
            return System.IO.Path.GetInvalidFileNameChars();
        }

        /// <summary>获取包含不能在路径名中指定的字符的数组。</summary>
        public static char[] GetInvalidPathChars()
        {
            return System.IO.Path.GetInvalidPathChars();
        }

        // =====================================================================
        // 私有辅助方法
        // =====================================================================

        private static string EnvOrNull(string name)
        {
            try
            {
                string val = Environment.GetEnvironmentVariable(name);
                return string.IsNullOrEmpty(val) ? null : val;
            }
            catch { return null; }
        }

        private static string CombineInternal(string root, string[] subPaths)
        {
            if (string.IsNullOrEmpty(root)) root = string.Empty;
            if (subPaths == null || subPaths.Length == 0) return root;

            string[] all = new string[subPaths.Length + 1];
            all[0] = root;
            Array.Copy(subPaths, 0, all, 1, subPaths.Length);
            return System.IO.Path.Combine(all);
        }

        private static string TryGetGameDirFromUnityOrProcess()
        {
            try
            {
                // Unity 运行时在 Windows 上 dataPath 为 <游戏目录>\AstralParty_CN_Data
                string dataPath = UnityEngine.Application.dataPath;
                if (!string.IsNullOrEmpty(dataPath))
                {
                    string parent = System.IO.Path.GetDirectoryName(dataPath.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parent)) return parent;
                }
            }
            catch { }
            return null;
        }
    }
}
