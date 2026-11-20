using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using UnityEngine;

namespace FontMod
{
    internal sealed class BuiltTmpFontBundle
    {
        internal object TmpFontAsset;
        internal object SourceUnityFont;
        internal Material ClonedMaterial;
        internal string SourceDescription;
        internal int WarmupGlyphCount;
        internal int FallbackFilledCount;
        internal int MissingGlyphCount;
        internal int AtlasWidth;
        internal int AtlasHeight;

        internal void Destroy()
        {
            DestroyUnityObj(ClonedMaterial);
            ClonedMaterial = null;
            DestroyUnityObj(TmpFontAsset);
            TmpFontAsset = null;
            DestroyUnityObj(SourceUnityFont);
            SourceUnityFont = null;
        }

        private static void DestroyUnityObj(object obj)
        {
            try
            {
                UnityEngine.Object uo = obj as UnityEngine.Object;
                if (uo != null) UnityEngine.Object.Destroy(uo);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 运行时动态构建单图集 Dynamic SDF TMP_FontAsset（含同图集回退补字、GPU 队列上传、材质兜底与字重配置）。
    /// 全程通过 IL2CPP 保留的反射 API 调用，编译期零依赖 Unity.TextMeshPro.dll。
    /// </summary>
    internal sealed class TmpFontFactory
    {
        private const int GlyphRenderModeSdfaa = 4165; // 0x1045 = GlyphRenderMode.SDFAA
        private const int AtlasPopulationModeDynamic = 1;

        internal Type FontType;
        internal Type TmpFontAssetType;
        internal Type TmpTextType;
        internal Type TmpCharacterType;
        internal Type FontEngineType;
        internal Type AtlasPopulationModeType;
        internal Type GlyphRenderModeType;
        internal Type GlyphPackingModeType;

        private MethodInfo _updateAtlasQueueMethod;
        private MethodInfo _setTextureUploadModeMethod;
        private bool _resolved;

        internal bool EnsureTypesResolved(ModLogger log)
        {
            if (_resolved) return TmpFontAssetType != null && FontType != null;

            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Assembly a = asms[i];
                if (FontType == null) FontType = a.GetType("UnityEngine.Font");
                if (TmpFontAssetType == null) TmpFontAssetType = a.GetType("TMPro.TMP_FontAsset");
                if (TmpTextType == null) TmpTextType = a.GetType("TMPro.TMP_Text");
                if (TmpCharacterType == null) TmpCharacterType = a.GetType("TMPro.TMP_Character");
                if (FontEngineType == null) FontEngineType = a.GetType("UnityEngine.TextCore.LowLevel.FontEngine");
                if (AtlasPopulationModeType == null) AtlasPopulationModeType = a.GetType("TMPro.AtlasPopulationMode");
                if (GlyphRenderModeType == null) GlyphRenderModeType = a.GetType("UnityEngine.TextCore.LowLevel.GlyphRenderMode");
                if (GlyphPackingModeType == null) GlyphPackingModeType = a.GetType("UnityEngine.TextCore.LowLevel.GlyphPackingMode");
            }

            if (TmpFontAssetType != null)
            {
                _updateAtlasQueueMethod = TmpFontAssetType.GetMethod(
                    "UpdateAtlasTexturesForFontAssetsInQueue",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);
            }

            if (FontEngineType != null)
            {
                _setTextureUploadModeMethod = FontEngineType.GetMethod(
                    "SetTextureUploadMode",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(bool) },
                    null);
            }

            _resolved = FontType != null && TmpFontAssetType != null;
            if (!_resolved && log != null)
            {
                log.Warn("尚未解析到 UnityEngine.Font / TMPro.TMP_FontAsset 运行时类型，稍后重试。");
            }
            return _resolved;
        }

        internal void FlushAtlasUploadQueue()
        {
            if (_updateAtlasQueueMethod == null) return;
            try { _updateAtlasQueueMethod.Invoke(null, null); }
            catch { }
        }

        internal void EnableImmediateTextureUpload()
        {
            if (_setTextureUploadModeMethod == null) return;
            try { _setTextureUploadModeMethod.Invoke(null, new object[] { true }); }
            catch { }
        }

        internal object CreateUnityFontFromPath(string filePath)
        {
            if (FontType == null || string.IsNullOrEmpty(filePath)) return null;
            try
            {
                ConstructorInfo ctor = FontType.GetConstructor(new[] { typeof(string) });
                if (ctor != null)
                {
                    object font = ctor.Invoke(new object[] { filePath });
                    if (font != null)
                    {
                        SetHideFlags(font);
                        return font;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        internal object CreateUnityFontFromOs(string family, string fallbackCsv, int size)
        {
            if (FontType == null || string.IsNullOrEmpty(family)) return null;
            try
            {
                if (!string.IsNullOrEmpty(fallbackCsv))
                {
                    MethodInfo arrMethod = FontType.GetMethod(
                        "CreateDynamicFontFromOSFont",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(string[]), typeof(int) },
                        null);
                    if (arrMethod != null)
                    {
                        string[] candidates = FontNames.BuildCandidates(family, fallbackCsv);
                        object font = arrMethod.Invoke(null, new object[] { candidates, size });
                        if (font != null)
                        {
                            SetHideFlags(font);
                            return font;
                        }
                    }
                }

                MethodInfo singleMethod = FontType.GetMethod(
                    "CreateDynamicFontFromOSFont",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(int) },
                    null);
                if (singleMethod != null)
                {
                    object font = singleMethod.Invoke(null, new object[] { family, size });
                    if (font != null)
                    {
                        SetHideFlags(font);
                        return font;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        internal BuiltTmpFontBundle BuildDynamicTmpFont(
            ResolvedFontFile resolvedFile,
            string fontFamily,
            string fallbackFontsCsv,
            int samplingPointSize,
            int atlasPadding,
            int atlasSize,
            float fontScale,
            float normalWeight,
            float boldWeight,
            bool warmupCharset,
            object templateOriginalAsset,
            ModLogger log)
        {
            if (!EnsureTypesResolved(log)) return null;

            EnableImmediateTextureUpload();

            bool preferOsFirst = resolvedFile != null && resolvedFile.FaceIndex > 0 && !string.IsNullOrEmpty(fontFamily);

            BuiltTmpFontBundle built = null;
            if (preferOsFirst)
            {
                built = TryBuildFromOsFont(fontFamily, resolvedFile, fallbackFontsCsv, samplingPointSize, atlasPadding, atlasSize, fontScale, normalWeight, boldWeight, warmupCharset, templateOriginalAsset, log);
                if (built == null && resolvedFile != null)
                {
                    built = TryBuildFromFilePath(resolvedFile, fallbackFontsCsv, samplingPointSize, atlasPadding, atlasSize, fontScale, normalWeight, boldWeight, warmupCharset, templateOriginalAsset, log);
                }
            }
            else
            {
                if (resolvedFile != null)
                {
                    built = TryBuildFromFilePath(resolvedFile, fallbackFontsCsv, samplingPointSize, atlasPadding, atlasSize, fontScale, normalWeight, boldWeight, warmupCharset, templateOriginalAsset, log);
                }
                if (built == null && !string.IsNullOrEmpty(fontFamily))
                {
                    built = TryBuildFromOsFont(fontFamily, resolvedFile, fallbackFontsCsv, samplingPointSize, atlasPadding, atlasSize, fontScale, normalWeight, boldWeight, warmupCharset, templateOriginalAsset, log);
                }
            }

            return built;
        }

        private BuiltTmpFontBundle TryBuildFromFilePath(
            ResolvedFontFile resolvedFile,
            string fallbackFontsCsv,
            int samplingPointSize,
            int atlasPadding,
            int atlasSize,
            float fontScale,
            float normalWeight,
            float boldWeight,
            bool warmupCharset,
            object templateOriginalAsset,
            ModLogger log)
        {
            object unityFont = CreateUnityFontFromPath(resolvedFile.FilePath);
            if (unityFont == null)
            {
                if (log != null) log.Warn("new Font(filePath) 失败: " + resolvedFile.FilePath);
                return null;
            }

            BuiltTmpFontBundle bundle = CreateAndValidateTmpAsset(
                unityFont,
                resolvedFile,
                fallbackFontsCsv,
                "file:" + resolvedFile.FilePath + " (face=" + resolvedFile.FaceIndex + ", " + resolvedFile.MatchedBy + ")",
                samplingPointSize,
                atlasPadding,
                atlasSize,
                fontScale,
                normalWeight,
                boldWeight,
                warmupCharset,
                templateOriginalAsset,
                log);

            if (bundle == null)
            {
                UnityEngine.Object uo = unityFont as UnityEngine.Object;
                if (uo != null) UnityEngine.Object.Destroy(uo);
            }
            return bundle;
        }

        private BuiltTmpFontBundle TryBuildFromOsFont(
            string fontFamily,
            ResolvedFontFile resolvedFile,
            string fallbackFontsCsv,
            int samplingPointSize,
            int atlasPadding,
            int atlasSize,
            float fontScale,
            float normalWeight,
            float boldWeight,
            bool warmupCharset,
            object templateOriginalAsset,
            ModLogger log)
        {
            object unityFont = CreateUnityFontFromOs(fontFamily, null, samplingPointSize);
            if (unityFont == null)
            {
                if (log != null) log.Warn("CreateDynamicFontFromOSFont 失败: " + fontFamily);
                return null;
            }

            BuiltTmpFontBundle bundle = CreateAndValidateTmpAsset(
                unityFont,
                resolvedFile,
                fallbackFontsCsv,
                "osFont:" + fontFamily,
                samplingPointSize,
                atlasPadding,
                atlasSize,
                fontScale,
                normalWeight,
                boldWeight,
                warmupCharset,
                templateOriginalAsset,
                log);

            if (bundle == null)
            {
                UnityEngine.Object uo = unityFont as UnityEngine.Object;
                if (uo != null) UnityEngine.Object.Destroy(uo);
            }
            return bundle;
        }

        private BuiltTmpFontBundle CreateAndValidateTmpAsset(
            object unityFont,
            ResolvedFontFile primaryFile,
            string fallbackFontsCsv,
            string desc,
            int samplingPointSize,
            int atlasPadding,
            int atlasSize,
            float fontScale,
            float normalWeight,
            float boldWeight,
            bool warmupCharset,
            object templateOriginalAsset,
            ModLogger log)
        {
            object tmpAsset = InvokeCreateFontAsset(unityFont, samplingPointSize, atlasPadding, atlasSize, log);
            if (tmpAsset == null) return null;

            try
            {
                SetPropertyIfExists(tmpAsset, "atlasPopulationMode", Enum.ToObject(AtlasPopulationModeType, AtlasPopulationModeDynamic));
                SetPropertyIfExists(tmpAsset, "isMultiAtlasTexturesEnabled", false);
                ClearFallbackList(tmpAsset);

                SetFieldIfExists(tmpAsset, "normalStyle", normalWeight);
                SetFieldIfExists(tmpAsset, "normalSpacingOffset", 0.0f);
                SetFieldIfExists(tmpAsset, "boldStyle", boldWeight);
                SetFieldIfExists(tmpAsset, "boldSpacing", 2.0f);
                SetFieldIfExists(tmpAsset, "italicStyle", (byte)15);

                if (Math.Abs(fontScale - 1.0f) > 0.001f)
                {
                    ApplyFaceInfoScale(tmpAsset, fontScale);
                }

                string warmup = warmupCharset ? GameCharset.Text : (GameCharset.CoreAscii + "一二三四五六七八九十中文星币技能卡牌");
                string missingChars = "";
                InvokeTryAddCharacters(tmpAsset, warmup, out missingChars);
                FlushAtlasUploadQueue();

                int fallbackFilled = 0;
                if (!string.IsNullOrEmpty(missingChars))
                {
                    fallbackFilled = FillMissingGlyphsInSameAtlas(
                        tmpAsset,
                        missingChars,
                        fallbackFontsCsv,
                        primaryFile,
                        samplingPointSize,
                        atlasPadding,
                        log);
                }

                int remainingMissing = string.IsNullOrEmpty(missingChars) ? 0 : Math.Max(0, missingChars.Length - fallbackFilled);

                Texture atlasTex = GetAtlasTexture(tmpAsset);
                int glyphCount = GetCharacterCount(tmpAsset);

                if (atlasTex == null || atlasTex.width < 128 || atlasTex.height < 128 || glyphCount < 10)
                {
                    if (log != null)
                    {
                        log.Warn("动态 TMP_FontAsset 校验未通过 (" + desc + "): glyphs=" + glyphCount
                            + ", atlas=" + (atlasTex == null ? "null" : (atlasTex.width + "x" + atlasTex.height)));
                    }
                    UnityEngine.Object uo = tmpAsset as UnityEngine.Object;
                    if (uo != null) UnityEngine.Object.Destroy(uo);
                    return null;
                }

                Material clonedMat = EnsureValidMaterial(tmpAsset, atlasTex, templateOriginalAsset, atlasPadding, log);

                SetHideFlags(tmpAsset);
                SetHideFlags(atlasTex);
                if (clonedMat != null) SetHideFlags(clonedMat);

                var bundle = new BuiltTmpFontBundle();
                bundle.TmpFontAsset = tmpAsset;
                bundle.SourceUnityFont = unityFont;
                bundle.ClonedMaterial = clonedMat;
                bundle.SourceDescription = desc;
                bundle.WarmupGlyphCount = glyphCount;
                bundle.FallbackFilledCount = fallbackFilled;
                bundle.MissingGlyphCount = remainingMissing;
                bundle.AtlasWidth = atlasTex.width;
                bundle.AtlasHeight = atlasTex.height;
                return bundle;
            }
            catch (Exception e)
            {
                if (log != null) log.Warn("初始化动态 TMP_FontAsset 异常 (" + desc + "): " + e.Message);
                UnityEngine.Object uo = tmpAsset as UnityEngine.Object;
                if (uo != null) UnityEngine.Object.Destroy(uo);
                return null;
            }
        }

        private object InvokeCreateFontAsset(object unityFont, int samplingPointSize, int atlasPadding, int atlasSize, ModLogger log)
        {
            try
            {
                if (GlyphRenderModeType != null && AtlasPopulationModeType != null)
                {
                    MethodInfo fullCtor = TmpFontAssetType.GetMethod(
                        "CreateFontAsset",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[]
                        {
                            FontType,
                            typeof(int),
                            typeof(int),
                            GlyphRenderModeType,
                            typeof(int),
                            typeof(int),
                            AtlasPopulationModeType,
                            typeof(bool)
                        },
                        null);

                    if (fullCtor != null)
                    {
                        object renderMode = Enum.ToObject(GlyphRenderModeType, GlyphRenderModeSdfaa);
                        object popMode = Enum.ToObject(AtlasPopulationModeType, AtlasPopulationModeDynamic);
                        return fullCtor.Invoke(null, new object[]
                        {
                            unityFont,
                            samplingPointSize,
                            atlasPadding,
                            renderMode,
                            atlasSize,
                            atlasSize,
                            popMode,
                            false
                        });
                    }
                }

                MethodInfo simple = TmpFontAssetType.GetMethod(
                    "CreateFontAsset",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { FontType },
                    null);
                if (simple != null)
                {
                    return simple.Invoke(null, new object[] { unityFont });
                }
            }
            catch (TargetInvocationException tie)
            {
                if (log != null) log.Warn("CreateFontAsset 内部异常: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message));
            }
            catch (Exception e)
            {
                if (log != null) log.Warn("CreateFontAsset 调用异常: " + e.Message);
            }
            return null;
        }

        private void InvokeTryAddCharacters(object tmpAsset, string chars, out string missingChars)
        {
            missingChars = "";
            try
            {
                MethodInfo[] methods = tmpAsset.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                MethodInfo best3 = null;
                MethodInfo best2 = null;
                for (int i = 0; i < methods.Length; i++)
                {
                    if (!string.Equals(methods[i].Name, "TryAddCharacters", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = methods[i].GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType == typeof(string) && ps[1].IsOut && ps[2].ParameterType == typeof(bool))
                    {
                        best3 = methods[i];
                        break;
                    }
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(bool))
                    {
                        best2 = methods[i];
                    }
                }

                if (best3 != null)
                {
                    object[] args = new object[] { chars, null, false };
                    best3.Invoke(tmpAsset, args);
                    missingChars = (args[1] as string) ?? "";
                    return;
                }
                if (best2 != null)
                {
                    best2.Invoke(tmpAsset, new object[] { chars, false });
                }
            }
            catch
            {
            }
        }

        private int FillMissingGlyphsInSameAtlas(
            object tmpAsset,
            string missingChars,
            string fallbackFontsCsv,
            ResolvedFontFile primaryFile,
            int samplingPointSize,
            int atlasPadding,
            ModLogger log)
        {
            if (FontEngineType == null || TmpCharacterType == null || GlyphPackingModeType == null || GlyphRenderModeType == null)
            {
                return 0;
            }

            try
            {
                Texture2D atlasTex = GetAtlasTexture(tmpAsset) as Texture2D;
                if (atlasTex == null) return 0;

                object freeRects = GetPropertyValue(tmpAsset, "freeGlyphRects");
                object usedRects = GetPropertyValue(tmpAsset, "usedGlyphRects");
                IList glyphTable = GetPropertyValue(tmpAsset, "glyphTable") as IList;
                IDictionary glyphLookup = GetPropertyValue(tmpAsset, "glyphLookupTable") as IDictionary;
                IList charTable = GetPropertyValue(tmpAsset, "characterTable") as IList;
                IDictionary charLookup = GetPropertyValue(tmpAsset, "characterLookupTable") as IDictionary;

                if (freeRects == null || usedRects == null || glyphTable == null || glyphLookup == null || charTable == null || charLookup == null)
                {
                    return 0;
                }

                MethodInfo loadFace3 = FontEngineType.GetMethod(
                    "LoadFontFace",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(int), typeof(int) },
                    null);
                MethodInfo getGlyphIdx = FontEngineType.GetMethod(
                    "GetGlyphIndex",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(uint) },
                    null);
                MethodInfo tryAddGlyph = null;
                MethodInfo[] feMethods = FontEngineType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < feMethods.Length; i++)
                {
                    if (!string.Equals(feMethods[i].Name, "TryAddGlyphToTexture", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = feMethods[i].GetParameters();
                    if (ps.Length == 8 && ps[0].ParameterType == typeof(uint))
                    {
                        tryAddGlyph = feMethods[i];
                        break;
                    }
                }

                if (loadFace3 == null || getGlyphIdx == null || tryAddGlyph == null) return 0;

                ConstructorInfo charCtor = null;
                ConstructorInfo[] ctors = TmpCharacterType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < ctors.Length; i++)
                {
                    ParameterInfo[] ps = ctors[i].GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType == typeof(uint))
                    {
                        charCtor = ctors[i];
                        break;
                    }
                }
                if (charCtor == null) return 0;

                var pending = new List<uint>();
                var seen = new HashSet<uint>();
                for (int i = 0; i < missingChars.Length; i++)
                {
                    uint u = (uint)missingChars[i];
                    if (u < 32 || charLookup.Contains(u)) continue;
                    if (seen.Add(u)) pending.Add(u);
                }
                if (pending.Count == 0) return 0;

                var fbFamilies = new List<string>();
                if (!string.IsNullOrEmpty(fallbackFontsCsv))
                {
                    string[] parts = fallbackFontsCsv.Split(',');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string p = parts[i].Trim();
                        if (p.Length > 0 && !fbFamilies.Contains(p)) fbFamilies.Add(p);
                    }
                }
                string[] defaults = new[] { "Microsoft YaHei", "SimHei", "DengXian", "SimSun" };
                for (int i = 0; i < defaults.Length; i++)
                {
                    if (!fbFamilies.Contains(defaults[i])) fbFamilies.Add(defaults[i]);
                }

                object packingMode = Enum.ToObject(GlyphPackingModeType, 0);
                object renderMode = Enum.ToObject(GlyphRenderModeType, GlyphRenderModeSdfaa);
                uint nextSyntheticGlyphIndex = 0x100000u;
                int totalFilled = 0;
                var usedFallbacks = new List<string>();

                PropertyInfo glyphIndexProp = null;
                PropertyInfo glyphAtlasIdxProp = null;
                PropertyInfo glyphScaleProp = null;

                for (int fi = 0; fi < fbFamilies.Count && pending.Count > 0; fi++)
                {
                    ResolvedFontFile fbFile = FontFileResolver.Resolve(
                        "", fbFamilies[fi], "", "", "", FontType, FontEngineType, null);
                    if (fbFile == null) continue;
                    if (primaryFile != null
                        && string.Equals(fbFile.FilePath, primaryFile.FilePath, StringComparison.OrdinalIgnoreCase)
                        && fbFile.FaceIndex == primaryFile.FaceIndex)
                    {
                        continue;
                    }

                    object errObj = loadFace3.Invoke(null, new object[] { fbFile.FilePath, samplingPointSize, fbFile.FaceIndex });
                    if (errObj == null || Convert.ToInt32(errObj) != 0) continue;

                    int filledByThis = 0;
                    var stillMissing = new List<uint>();

                    for (int ci = 0; ci < pending.Count; ci++)
                    {
                        uint unicode = pending[ci];
                        uint srcGlyphIdx = (uint)getGlyphIdx.Invoke(null, new object[] { unicode });
                        if (srcGlyphIdx == 0)
                        {
                            stillMissing.Add(unicode);
                            continue;
                        }

                        object[] addArgs = new object[]
                        {
                            srcGlyphIdx,
                            atlasPadding,
                            packingMode,
                            freeRects,
                            usedRects,
                            renderMode,
                            atlasTex,
                            null
                        };
                        object okObj = tryAddGlyph.Invoke(null, addArgs);
                        object glyph = addArgs[7];
                        if (!(okObj is bool) || !(bool)okObj || glyph == null)
                        {
                            stillMissing.Add(unicode);
                            continue;
                        }

                        if (glyphIndexProp == null)
                        {
                            Type gt = glyph.GetType();
                            glyphIndexProp = gt.GetProperty("index", BindingFlags.Public | BindingFlags.Instance);
                            glyphAtlasIdxProp = gt.GetProperty("atlasIndex", BindingFlags.Public | BindingFlags.Instance);
                            glyphScaleProp = gt.GetProperty("scale", BindingFlags.Public | BindingFlags.Instance);
                        }

                        uint synthIdx = nextSyntheticGlyphIndex++;
                        while (glyphLookup.Contains(synthIdx)) synthIdx = nextSyntheticGlyphIndex++;

                        if (glyphIndexProp != null) glyphIndexProp.SetValue(glyph, synthIdx, null);
                        if (glyphAtlasIdxProp != null) glyphAtlasIdxProp.SetValue(glyph, 0, null);
                        if (glyphScaleProp != null) glyphScaleProp.SetValue(glyph, 1.0f, null);

                        glyphTable.Add(glyph);
                        glyphLookup[synthIdx] = glyph;

                        object tmpChar = charCtor.Invoke(new object[] { unicode, tmpAsset, glyph });
                        if (tmpChar != null)
                        {
                            charTable.Add(tmpChar);
                            charLookup[unicode] = tmpChar;
                            filledByThis++;
                            totalFilled++;
                        }
                    }

                    if (filledByThis > 0) usedFallbacks.Add(fbFamilies[fi] + "+" + filledByThis);
                    pending = stillMissing;
                }

                if (totalFilled > 0)
                {
                    atlasTex.Apply(false, false);
                    if (log != null)
                    {
                        log.Info("已执行同图集 SDF 回退补字: 共补齐 " + totalFilled + " 字 ("
                            + string.Join(", ", usedFallbacks.ToArray()) + "), 仍缺 " + pending.Count + " 字");
                    }
                }

                if (primaryFile != null)
                {
                    loadFace3.Invoke(null, new object[] { primaryFile.FilePath, samplingPointSize, primaryFile.FaceIndex });
                }

                return totalFilled;
            }
            catch (Exception e)
            {
                if (log != null) log.Warn("同图集 SDF 回退补字异常: " + e.Message);
                return 0;
            }
        }

        private Material EnsureValidMaterial(object tmpAsset, Texture atlasTex, object templateOriginalAsset, int atlasPadding, ModLogger log)
        {
            FieldInfo matField = GetFieldRecursive(tmpAsset.GetType(), "material");
            if (matField == null) return null;

            Material current = matField.GetValue(tmpAsset) as Material;
            if (current != null && current.shader != null)
            {
                if (current.mainTexture == null) current.mainTexture = atlasTex;
                SetHideFlags(current);
                return null;
            }

            Material templateMat = templateOriginalAsset != null ? (matField.GetValue(templateOriginalAsset) as Material) : null;
            Material created = null;
            if (templateMat != null)
            {
                created = new Material(templateMat);
            }
            else
            {
                Shader sh = Shader.Find("TextMeshPro/Distance Field");
                if (sh == null) sh = Shader.Find("TextMeshPro/Mobile/Distance Field");
                if (sh == null) sh = Shader.Find("FairyGUI/TextMeshPro/Distance Field");
                if (sh != null) created = new Material(sh);
            }

            if (created != null)
            {
                created.mainTexture = atlasTex;
                if (created.HasProperty("_TextureWidth")) created.SetFloat("_TextureWidth", atlasTex.width);
                if (created.HasProperty("_TextureHeight")) created.SetFloat("_TextureHeight", atlasTex.height);
                if (created.HasProperty("_GradientScale")) created.SetFloat("_GradientScale", atlasPadding + 1);
                matField.SetValue(tmpAsset, created);
                if (log != null) log.Info("已为动态 TMP_FontAsset 补充克隆材质: " + (created.shader != null ? created.shader.name : "(null)"));
            }
            return created;
        }

        private static Texture GetAtlasTexture(object tmpAsset)
        {
            PropertyInfo p = tmpAsset.GetType().GetProperty("atlasTexture", BindingFlags.Public | BindingFlags.Instance);
            return p != null ? p.GetValue(tmpAsset, null) as Texture : null;
        }

        private static int GetCharacterCount(object tmpAsset)
        {
            PropertyInfo p = tmpAsset.GetType().GetProperty("characterTable", BindingFlags.Public | BindingFlags.Instance);
            if (p != null)
            {
                ICollection col = p.GetValue(tmpAsset, null) as ICollection;
                if (col != null) return col.Count;
            }
            return 0;
        }

        private static void ClearFallbackList(object tmpAsset)
        {
            try
            {
                PropertyInfo p = tmpAsset.GetType().GetProperty("fallbackFontAssetTable", BindingFlags.Public | BindingFlags.Instance);
                if (p != null)
                {
                    IList list = p.GetValue(tmpAsset, null) as IList;
                    if (list != null) list.Clear();
                }
            }
            catch
            {
            }
        }

        private static void ApplyFaceInfoScale(object tmpAsset, float factor)
        {
            try
            {
                PropertyInfo fiProp = tmpAsset.GetType().GetProperty("faceInfo", BindingFlags.Public | BindingFlags.Instance);
                if (fiProp == null || !fiProp.CanRead || !fiProp.CanWrite) return;
                object faceInfo = fiProp.GetValue(tmpAsset, null);
                if (faceInfo == null) return;
                PropertyInfo scaleProp = faceInfo.GetType().GetProperty("scale", BindingFlags.Public | BindingFlags.Instance);
                if (scaleProp == null || !scaleProp.CanRead || !scaleProp.CanWrite) return;
                float cur = (float)scaleProp.GetValue(faceInfo, null);
                if (cur <= 0f) cur = 1f;
                scaleProp.SetValue(faceInfo, cur * factor, null);
                fiProp.SetValue(tmpAsset, faceInfo, null);
            }
            catch
            {
            }
        }

        private static object GetPropertyValue(object obj, string name)
        {
            PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p != null ? p.GetValue(obj, null) : null;
        }

        private static void SetFieldIfExists(object dst, string fieldName, object value)
        {
            try
            {
                FieldInfo f = GetFieldRecursive(dst.GetType(), fieldName);
                if (f != null) f.SetValue(dst, value);
            }
            catch
            {
            }
        }

        private static FieldInfo GetFieldRecursive(Type t, string name)
        {
            while (t != null)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        private static void SetPropertyIfExists(object obj, string name, object value)
        {
            try
            {
                PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite) p.SetValue(obj, value, null);
            }
            catch
            {
            }
        }

        private static void SetHideFlags(object obj)
        {
            UnityEngine.Object uo = obj as UnityEngine.Object;
            if (uo != null) uo.hideFlags = HideFlags.HideAndDontSave;
        }
    }
}
