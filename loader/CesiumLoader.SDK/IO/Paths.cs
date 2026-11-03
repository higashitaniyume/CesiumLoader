using System;

namespace CesiumLoader.SDK.IO
{
    /// <summary>
    /// <see cref="Path"/> 的复数别名类。
    /// 当模组源码中同时存在 <c>using System.IO;</c> 与 <c>using CesiumLoader.SDK.IO;</c> 时，
    /// 可使用 <see cref="Paths"/> 避免与 <see cref="System.IO.Path"/> 产生编译器歧义。
    /// </summary>
    public static class Paths
    {
        public static char DirectorySeparatorChar { get { return Path.DirectorySeparatorChar; } }
        public static char AltDirectorySeparatorChar { get { return Path.AltDirectorySeparatorChar; } }
        public static char PathSeparator { get { return Path.PathSeparator; } }
        public static char VolumeSeparatorChar { get { return Path.VolumeSeparatorChar; } }

        public static string LoaderDirectory { get { return Path.LoaderDirectory; } }
        public static string LoaderRootDirectory { get { return Path.LoaderRootDirectory; } }
        public static string GameDirectory { get { return Path.GameDirectory; } }
        public static string GameRootDirectory { get { return Path.GameRootDirectory; } }
        public static string ModsDirectory { get { return Path.ModsDirectory; } }
        public static string ConfigsDirectory { get { return Path.ConfigsDirectory; } }
        public static string LogsDirectory { get { return Path.LogsDirectory; } }
        public static string SkinsDirectory { get { return Path.SkinsDirectory; } }
        public static string AudioDirectory { get { return Path.AudioDirectory; } }
        public static string AudioSchemesDirectory { get { return Path.AudioSchemesDirectory; } }
        public static string SpeedDirectory { get { return Path.SpeedDirectory; } }
        public static string WindowDirectory { get { return Path.WindowDirectory; } }
        public static string SdkDirectory { get { return Path.SdkDirectory; } }
        public static string BootstrapDirectory { get { return Path.BootstrapDirectory; } }
        public static string CurrentModDirectory { get { return Path.CurrentModDirectory; } }

        public static string CurrentLogFile { get { return Path.CurrentLogFile; } }
        public static string CurrentActivityLogFile { get { return Path.CurrentActivityLogFile; } }
        public static string CurrentErrorLogFile { get { return Path.CurrentErrorLogFile; } }

        public static string GetGamePath(params string[] subPaths) { return Path.GetGamePath(subPaths); }
        public static string GetLoaderPath(params string[] subPaths) { return Path.GetLoaderPath(subPaths); }
        public static string GetModDirectory(string modId) { return Path.GetModDirectory(modId); }
        public static string GetModPath(string modId, params string[] subPaths) { return Path.GetModPath(modId, subPaths); }
        public static string GetModPath(params string[] subPaths) { return Path.GetModPath(subPaths); }
        public static string GetModDataDirectory(string modId) { return Path.GetModDataDirectory(modId); }
        public static string GetModDataPath(string modId, params string[] subPaths) { return Path.GetModDataPath(modId, subPaths); }
        public static string GetModConfigPath(string modId) { return Path.GetModConfigPath(modId); }
        public static string GetConfigPath(string fileNameOrModId) { return Path.GetConfigPath(fileNameOrModId); }
        public static string GetLogPath(params string[] subPaths) { return Path.GetLogPath(subPaths); }
        public static string GetSkinDirectory(string skinName) { return Path.GetSkinDirectory(skinName); }
        public static string GetSkinPath(string skinName, params string[] subPaths) { return Path.GetSkinPath(skinName, subPaths); }
        public static string GetAudioPath(params string[] subPaths) { return Path.GetAudioPath(subPaths); }
        public static string GetAudioSchemeDirectory(string schemeId) { return Path.GetAudioSchemeDirectory(schemeId); }
        public static string GetAudioSchemePath(string schemeId, params string[] subPaths) { return Path.GetAudioSchemePath(schemeId, subPaths); }

        public static string EnsureDirectoryExists(string directoryPath) { return Path.EnsureDirectoryExists(directoryPath); }
        public static string SanitizeFileName(string fileName, char replacement = '_') { return Path.SanitizeFileName(fileName, replacement); }
        public static string Normalize(string path) { return Path.Normalize(path); }

        public static string Combine(string path1, string path2) { return Path.Combine(path1, path2); }
        public static string Combine(string path1, string path2, string path3) { return Path.Combine(path1, path2, path3); }
        public static string Combine(string path1, string path2, string path3, string path4) { return Path.Combine(path1, path2, path3, path4); }
        public static string Combine(params string[] paths) { return Path.Combine(paths); }

        public static string GetDirectoryName(string path) { return Path.GetDirectoryName(path); }
        public static string GetFileName(string path) { return Path.GetFileName(path); }
        public static string GetFileNameWithoutExtension(string path) { return Path.GetFileNameWithoutExtension(path); }
        public static string GetExtension(string path) { return Path.GetExtension(path); }
        public static string ChangeExtension(string path, string extension) { return Path.ChangeExtension(path, extension); }
        public static string GetFullPath(string path) { return Path.GetFullPath(path); }
        public static bool HasExtension(string path) { return Path.HasExtension(path); }
        public static string GetPathRoot(string path) { return Path.GetPathRoot(path); }
        public static bool IsPathRooted(string path) { return Path.IsPathRooted(path); }
        public static char[] GetInvalidFileNameChars() { return Path.GetInvalidFileNameChars(); }
        public static char[] GetInvalidPathChars() { return Path.GetInvalidPathChars(); }
    }
}
