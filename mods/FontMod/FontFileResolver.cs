using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using CesiumLoader.SDK.Logging;

namespace FontMod
{
    internal sealed class ResolvedFontFile
    {
        internal string FilePath;
        internal int FaceIndex;
        internal string MatchedBy;
    }

    /// <summary>
    /// 把配置里的本地字体路径或系统/用户字体名解析为磁盘上的 .ttf / .otf / .ttc 路径与 faceIndex，
    /// 同时提供用户目录（%LOCALAPPDATA%\Microsoft\Windows\Fonts）家族名枚举，用于导出完整的 Toys 字体清单。
    /// </summary>
    internal static class FontFileResolver
    {
        internal sealed class FontFaceRecord
        {
            internal string FilePath;
            internal int FaceIndex;
            internal List<string> Names;
            internal List<string> RawFamilyNames;
            internal List<string> Styles;
        }

        private static readonly string[] StyleSuffixes =
        {
            "ExtraLight", "ExtraBold", "DemiBold", "SemiBold", "UltraLight", "UltraBold",
            "Regular", "Normal", "Medium", "Light", "Bold", "Thin", "Black", "Heavy", "Italic", "Oblique",
            "细体", "常规", "中等", "粗体", "特粗", "极细"
        };

        private static List<FontFaceRecord> _cachedRecords;

        /// <summary>
        /// 枚举用户字体目录（%LOCALAPPDATA%\Microsoft\Windows\Fonts）下所有字体的代表家族名
        ///（优先 nameID=16 TypographicFamily，与 Toys 的 WPF 字体匹配逻辑完全对齐）。
        /// </summary>
        internal static List<string> CollectUserFontFamilyNames(string[] alreadyIncluded)
        {
            var result = new List<string>();
            try
            {
                var existingNorm = new HashSet<string>(StringComparer.Ordinal);
                if (alreadyIncluded != null)
                {
                    for (int i = 0; i < alreadyIncluded.Length; i++)
                    {
                        string norm = NormalizeName(alreadyIncluded[i]);
                        if (norm.Length > 0) existingNorm.Add(norm);
                    }
                }

                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(localApp)) return result;

                string userFontDir = Path.Combine(Path.Combine(localApp, "Microsoft"), Path.Combine("Windows", "Fonts"));
                if (!Directory.Exists(userFontDir)) return result;

                var userFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddFontsFromDirectory(userFiles, userFontDir);

                var userRecords = new List<FontFaceRecord>();
                foreach (string f in userFiles)
                {
                    try { ParseFontFile(f, userRecords); }
                    catch { }
                }

                for (int i = 0; i < userRecords.Count; i++)
                {
                    FontFaceRecord rec = userRecords[i];
                    if (rec.RawFamilyNames == null || rec.RawFamilyNames.Count == 0) continue;

                    bool alreadyCovered = false;
                    for (int j = 0; j < rec.RawFamilyNames.Count; j++)
                    {
                        if (existingNorm.Contains(NormalizeName(rec.RawFamilyNames[j])))
                        {
                            alreadyCovered = true;
                            break;
                        }
                    }
                    if (alreadyCovered) continue;

                    result.Add(rec.RawFamilyNames[0]);
                    for (int j = 0; j < rec.RawFamilyNames.Count; j++)
                    {
                        existingNorm.Add(NormalizeName(rec.RawFamilyNames[j]));
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        internal static ResolvedFontFile Resolve(
            string explicitPath,
            string fontFamily,
            string fontStyle,
            string modRoot,
            string loaderRoot,
            Type fontType,
            Type fontEngineType,
            ModLogger log)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                string full = ResolveExistingFile(explicitPath, modRoot, loaderRoot);
                if (full != null)
                {
                    var r = new ResolvedFontFile();
                    r.FilePath = full;
                    r.FaceIndex = 0;
                    r.MatchedBy = "explicit fontPath";
                    return r;
                }
                if (log != null) log.Warn("找不到配置的字体文件: " + explicitPath);
                return null;
            }

            if (string.IsNullOrEmpty(fontFamily)) return null;

            ResolvedFontFile nativeMatch = TryResolveViaFontEngine(fontEngineType, fontFamily, fontStyle);
            if (nativeMatch != null) return nativeMatch;

            EnsureIndexBuilt(fontType, log);
            return MatchFromIndex(fontFamily, fontStyle);
        }

        private static string ResolveExistingFile(string rawPath, string modRoot, string loaderRoot)
        {
            try
            {
                string trimmed = rawPath.Trim().Trim('"');
                if (File.Exists(trimmed)) return Path.GetFullPath(trimmed);
                if (!Path.IsPathRooted(trimmed))
                {
                    if (!string.IsNullOrEmpty(modRoot))
                    {
                        string p1 = Path.Combine(modRoot, trimmed);
                        if (File.Exists(p1)) return Path.GetFullPath(p1);
                    }
                    if (!string.IsNullOrEmpty(loaderRoot))
                    {
                        string p2 = Path.Combine(loaderRoot, trimmed);
                        if (File.Exists(p2)) return Path.GetFullPath(p2);
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static ResolvedFontFile TryResolveViaFontEngine(Type fontEngineType, string family, string style)
        {
            if (fontEngineType == null) return null;
            try
            {
                MethodInfo init = fontEngineType.GetMethod(
                    "InitializeFontEngine", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (init != null) init.Invoke(null, null);

                MethodInfo tryGet = null;
                MethodInfo[] methods = fontEngineType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (!string.Equals(methods[i].Name, "TryGetSystemFontReference", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = methods[i].GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                    {
                        tryGet = methods[i];
                        break;
                    }
                }
                if (tryGet == null) return null;

                Type refType = tryGet.GetParameters()[2].ParameterType.GetElementType();
                if (refType == null) return null;

                var pairs = BuildFamilyStylePairs(family, style);
                for (int i = 0; i < pairs.Count; i++)
                {
                    object[] args = new object[] { pairs[i].Key, pairs[i].Value, Activator.CreateInstance(refType) };
                    object okObj = tryGet.Invoke(null, args);
                    if (okObj is bool && (bool)okObj && args[2] != null)
                    {
                        string fp = ReadStringFieldOrProp(args[2], "filePath");
                        int faceIdx = ReadIntFieldOrProp(args[2], "faceIndex", 0);
                        if (!string.IsNullOrEmpty(fp) && File.Exists(fp))
                        {
                            var r = new ResolvedFontFile();
                            r.FilePath = fp;
                            r.FaceIndex = faceIdx;
                            r.MatchedBy = "FontEngine(" + pairs[i].Key + "/" + pairs[i].Value + ")";
                            return r;
                        }
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        internal static List<KeyValuePair<string, string>> BuildFamilyStylePairs(string family, string explicitStyle)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(explicitStyle))
            {
                list.Add(new KeyValuePair<string, string>(family, explicitStyle));
            }
            list.Add(new KeyValuePair<string, string>(family, "Regular"));
            list.Add(new KeyValuePair<string, string>(family, ""));
            list.Add(new KeyValuePair<string, string>(family, "Normal"));
            list.Add(new KeyValuePair<string, string>(family, "Medium"));

            string trimmed = family.Trim();
            for (int i = 0; i < StyleSuffixes.Length; i++)
            {
                string suf = StyleSuffixes[i];
                if (trimmed.Length > suf.Length + 1
                    && trimmed.EndsWith(" " + suf, StringComparison.OrdinalIgnoreCase))
                {
                    string baseName = trimmed.Substring(0, trimmed.Length - suf.Length - 1).Trim();
                    list.Add(new KeyValuePair<string, string>(baseName, suf));
                }
            }
            return list;
        }

        private static void EnsureIndexBuilt(Type fontType, ModLogger log)
        {
            if (_cachedRecords != null) return;
            _cachedRecords = new List<FontFaceRecord>();

            var pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (fontType != null)
            {
                try
                {
                    MethodInfo m = fontType.GetMethod(
                        "GetPathsToOSFonts", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (m != null)
                    {
                        string[] paths = m.Invoke(null, null) as string[];
                        if (paths != null)
                        {
                            for (int i = 0; i < paths.Length; i++)
                            {
                                if (!string.IsNullOrEmpty(paths[i])) pathSet.Add(paths[i]);
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            AddFontsFromDirectory(pathSet, Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApp))
            {
                AddFontsFromDirectory(pathSet, Path.Combine(Path.Combine(localApp, "Microsoft"), Path.Combine("Windows", "Fonts")));
            }

            foreach (string file in pathSet)
            {
                try { ParseFontFile(file, _cachedRecords); }
                catch { }
            }

            if (log != null)
            {
                log.Info("已扫描本地系统与用户字体索引: " + pathSet.Count + " 个文件, " + _cachedRecords.Count + " 个字面");
            }
        }

        private static void AddFontsFromDirectory(HashSet<string> set, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                string[] files = Directory.GetFiles(dir);
                for (int i = 0; i < files.Length; i++)
                {
                    string ext = Path.GetExtension(files[i]);
                    if (string.Equals(ext, ".ttf", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ext, ".otf", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ext, ".ttc", StringComparison.OrdinalIgnoreCase))
                    {
                        set.Add(files[i]);
                    }
                }
            }
            catch
            {
            }
        }

        private static ResolvedFontFile MatchFromIndex(string family, string explicitStyle)
        {
            if (_cachedRecords == null || _cachedRecords.Count == 0) return null;

            string normTarget = NormalizeName(family);
            string normStyle = NormalizeName(explicitStyle);

            string splitBase = null;
            string splitStyle = null;
            string trimmed = family.Trim();
            for (int i = 0; i < StyleSuffixes.Length; i++)
            {
                string suf = StyleSuffixes[i];
                if (trimmed.Length > suf.Length + 1
                    && trimmed.EndsWith(" " + suf, StringComparison.OrdinalIgnoreCase))
                {
                    splitBase = NormalizeName(trimmed.Substring(0, trimmed.Length - suf.Length - 1));
                    splitStyle = NormalizeName(suf);
                    break;
                }
            }

            FontFaceRecord best = null;
            int bestScore = -1;

            for (int i = 0; i < _cachedRecords.Count; i++)
            {
                FontFaceRecord rec = _cachedRecords[i];
                int score = ScoreRecord(rec, normTarget, normStyle, splitBase, splitStyle);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = rec;
                }
            }

            if (best == null || bestScore < 50) return null;

            var r = new ResolvedFontFile();
            r.FilePath = best.FilePath;
            r.FaceIndex = best.FaceIndex;
            r.MatchedBy = "OpenTypeNameTable(score=" + bestScore + ")";
            return r;
        }

        private static int ScoreRecord(
            FontFaceRecord rec,
            string normTarget,
            string normStyle,
            string splitBase,
            string splitStyle)
        {
            int best = -1;
            bool isRegular = IsRegularStyle(rec.Styles);

            for (int i = 0; i < rec.Names.Count; i++)
            {
                string n = rec.Names[i];
                if (string.Equals(n, normTarget, StringComparison.Ordinal))
                {
                    int s = 100;
                    if (!string.IsNullOrEmpty(normStyle))
                    {
                        if (HasStyle(rec.Styles, normStyle)) s += 30;
                    }
                    else if (isRegular)
                    {
                        s += 20;
                    }
                    if (rec.FaceIndex == 0) s += 2;
                    if (s > best) best = s;
                }
                else if (splitBase != null && string.Equals(n, splitBase, StringComparison.Ordinal))
                {
                    int s = 70;
                    if (HasStyle(rec.Styles, splitStyle)) s += 45;
                    if (s > best) best = s;
                }
            }

            string fileBase = NormalizeName(Path.GetFileNameWithoutExtension(rec.FilePath));
            if (string.Equals(fileBase, normTarget, StringComparison.Ordinal))
            {
                int s = 60 + (rec.FaceIndex == 0 ? 5 : 0);
                if (s > best) best = s;
            }

            return best;
        }

        private static bool IsRegularStyle(List<string> styles)
        {
            if (styles == null || styles.Count == 0) return true;
            for (int i = 0; i < styles.Count; i++)
            {
                string s = styles[i];
                if (s == "regular" || s == "normal" || s == "常规" || s == "标准") return true;
            }
            return false;
        }

        private static bool HasStyle(List<string> styles, string normStyle)
        {
            if (styles == null || string.IsNullOrEmpty(normStyle)) return false;
            for (int i = 0; i < styles.Count; i++)
            {
                if (string.Equals(styles[i], normStyle, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 解析一个字体文件的 name 表。整文件一次性读入后用偏移索引，
        /// 不碰 FileStream / FileInfo / Seek —— 这些在 HybridCLR 热更侧没有保证。
        /// </summary>
        internal static void ParseFontFile(string filePath, List<FontFaceRecord> output)
        {
            byte[] data = File.ReadAllBytes(filePath);
            if (data.Length < 12) return;

            uint tag = ReadUInt32BE(data, 0);
            if (tag == 0x74746366) // 'ttcf'
            {
                uint numFonts = ReadUInt32BE(data, 8);
                if (numFonts == 0 || numFonts > 64) return;
                if (12L + numFonts * 4L > data.Length) return;

                for (int fi = 0; fi < (int)numFonts; fi++)
                {
                    uint sfntOffset = ReadUInt32BE(data, 12 + fi * 4);
                    FontFaceRecord rec = ParseSfntFace(data, filePath, fi, sfntOffset);
                    if (rec != null) output.Add(rec);
                }
            }
            else if (tag == 0x00010000 || tag == 0x4F54544F || tag == 0x74727565)
            {
                FontFaceRecord rec = ParseSfntFace(data, filePath, 0, 0);
                if (rec != null) output.Add(rec);
            }
        }

        private static FontFaceRecord ParseSfntFace(byte[] data, string filePath, int faceIndex, uint sfntOffset)
        {
            long faceStart = sfntOffset;
            if (faceStart < 0 || faceStart + 12 > data.Length) return null;

            int hdr = (int)faceStart;
            ushort numTables = ReadUInt16BE(data, hdr + 4);
            if (numTables == 0 || numTables > 128) return null;

            long dirStart = faceStart + 12;
            if (dirStart + numTables * 16L > data.Length) return null;

            int dir = (int)dirStart;
            uint nameOffset = 0;
            uint nameLength = 0;
            for (int i = 0; i < numTables; i++)
            {
                uint ttag = ReadUInt32BE(data, dir + i * 16);
                if (ttag == 0x6E616D65) // 'name'
                {
                    nameOffset = ReadUInt32BE(data, dir + i * 16 + 8);
                    nameLength = ReadUInt32BE(data, dir + i * 16 + 12);
                    break;
                }
            }
            if (nameOffset == 0 || nameLength < 6 || nameLength > 2 * 1024 * 1024) return null;
            if ((long)nameOffset + nameLength > data.Length) return null;

            int bufBase = (int)nameOffset;
            int bufLen = (int)nameLength;

            ushort count = ReadUInt16BE(data, bufBase + 2);
            ushort stringOffset = ReadUInt16BE(data, bufBase + 4);
            if (6L + count * 12L > bufLen) return null;

            var names = new List<string>();
            var rawFamilyNames = new List<string>();
            var styles = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var seenRawFamily = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenStyles = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
            {
                int ro = bufBase + 6 + i * 12;
                ushort platformId = ReadUInt16BE(data, ro);
                ushort encodingId = ReadUInt16BE(data, ro + 2);
                ushort langId = ReadUInt16BE(data, ro + 4);
                ushort nameId = ReadUInt16BE(data, ro + 6);
                ushort length = ReadUInt16BE(data, ro + 8);
                ushort offset = ReadUInt16BE(data, ro + 10);

                if (nameId != 1 && nameId != 2 && nameId != 4 && nameId != 6 && nameId != 16 && nameId != 17) continue;

                int strPos = stringOffset + offset;
                if (strPos < 0 || strPos + length > bufLen) continue;

                string decoded = DecodeNameString(data, bufBase + strPos, length, platformId, encodingId);
                if (string.IsNullOrEmpty(decoded)) continue;

                string norm = NormalizeName(decoded);
                if (norm.Length == 0) continue;

                if (nameId == 2 || nameId == 17)
                {
                    if (seenStyles.Add(norm)) styles.Add(norm);
                }
                else
                {
                    if (seenNames.Add(norm)) names.Add(norm);
                    if ((nameId == 1 || nameId == 16) && (platformId == 3 || platformId == 0))
                    {
                        if (langId == 0x0409 || langId == 0x0804 || langId == 0x0404 || rawFamilyNames.Count == 0)
                        {
                            if (seenRawFamily.Add(decoded))
                            {
                                if (nameId == 16) rawFamilyNames.Insert(0, decoded);
                                else rawFamilyNames.Add(decoded);
                            }
                        }
                    }
                }
            }

            if (names.Count == 0) return null;
            var rec = new FontFaceRecord();
            rec.FilePath = filePath;
            rec.FaceIndex = faceIndex;
            rec.Names = names;
            rec.RawFamilyNames = rawFamilyNames;
            rec.Styles = styles;
            return rec;
        }

        private static string DecodeNameString(byte[] data, int offset, int length, ushort platformId, ushort encodingId)
        {
            try
            {
                if (platformId == 3 || platformId == 0)
                {
                    return Encoding.BigEndianUnicode.GetString(data, offset, length).Trim('\0', ' ');
                }
                if (platformId == 1 && encodingId == 0)
                {
                    return Encoding.ASCII.GetString(data, offset, length).Trim('\0', ' ');
                }
            }
            catch
            {
            }
            return null;
        }

        internal static string NormalizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c) || c == '-' || c == '_') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static ushort ReadUInt16BE(byte[] b, int o)
        {
            return (ushort)((b[o] << 8) | b[o + 1]);
        }

        private static uint ReadUInt32BE(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        private static string ReadStringFieldOrProp(object obj, string name)
        {
            Type t = obj.GetType();
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(obj) as string;
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj, null) as string;
            return null;
        }

        private static int ReadIntFieldOrProp(object obj, string name, int fallback)
        {
            Type t = obj.GetType();
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && f.GetValue(obj) is int) return (int)f.GetValue(obj);
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.GetValue(obj, null) is int) return (int)p.GetValue(obj, null);
            return fallback;
        }
    }
}
