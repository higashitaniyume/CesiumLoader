using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.UserInterface;
using FairyGUI;
using UnityEngine;

namespace FontMod
{
    /// <summary>
    /// FontMod v1.1.0 统一字体替换引擎：
    /// 1. 导出「Unity 系统字体 + Windows 用户目录字体」完整清单给 Toys 字体页；
    /// 2. 动态构建单图集 SDF TMP_FontAsset（含同图集回退补字），原地替换所有 FairyGUI _TMP 字体；
    /// 3. 默认通过 DynamicBridgeTmpFont 将旧版 DynamicFont 统一桥接到高清 SDF（兼容用户字体与本地 .ttf/.otf，
    ///    并自动清零遗留 faceDilate=1.0），同时热切换 Stage.inst 上已有文本；
    /// 4. 关闭 bridgeDynamicToSdf 时完整保留 v1.0.0 经典 DynamicFont.nativeFont 替换路径；
    /// 5. 同步替换场景内 3D TMPro.TMP_Text，并推送字体给 CesiumLoader SDK 自绘浮层（WidgetService）。
    /// </summary>
    internal sealed class FontReplacement
    {
        private const int MaxMissingGlyphs = 25;
        private const int MaxProbeMissing = 2;

        /// <summary>场景 TMP_Text 补扫间隔（秒）。首次替换后仅在低频补扫新实例化对象。</summary>
        private const double SceneTmpRefreshSeconds = 5.0;
        private const string WarmupText = "永和九年，岁在癸丑。星币筹码技能攻击防御0123456789ABCabc";
        private const string CoreChineseSample =
            "中文的一是在不了有和人这大上个国我以要他时来用们生到作地于出就分对成会可主发年动同工也能下过子说产种";

        private static readonly HashSet<string> DecorativeFontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SHOWG_TMP",
            "hp02_TMP",
            "dgo_TMP",
            "Impact_TMP",
            "Token_TMP",
            "SHOWG",
            "Impact",
            "VonwaonBitmap"
        };

        private static readonly PropertyInfo DynamicNativeFontProp =
            typeof(DynamicFont).GetProperty("nativeFont", BindingFlags.Public | BindingFlags.Instance);

        private static readonly FieldInfo BaseFontTextRebuildFlagField =
            typeof(BaseFont).GetField("textRebuildFlag", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo TextFieldFontField =
            typeof(TextField).GetField("_font", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TextFieldFontVersionField =
            typeof(TextField).GetField("_fontVersion", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TextFieldTextChangedField =
            typeof(TextField).GetField("_textChanged", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly TmpFontFactory _factory = new TmpFontFactory();

        // 原生 TMPFont 原始资产备份
        private readonly Dictionary<BaseFont, object> _originalTmpAssets = new Dictionary<BaseFont, object>();
        // 桥接模式：旧版 DynamicFont 备份与映射
        private readonly Dictionary<string, BaseFont> _originalDynamicByKey = new Dictionary<string, BaseFont>(StringComparer.Ordinal);
        private readonly Dictionary<string, DynamicBridgeTmpFont> _bridgeByKey = new Dictionary<string, DynamicBridgeTmpFont>(StringComparer.Ordinal);
        private readonly Dictionary<BaseFont, DynamicBridgeTmpFont> _oldDynamicToBridge = new Dictionary<BaseFont, DynamicBridgeTmpFont>();
        private readonly Dictionary<DynamicBridgeTmpFont, BaseFont> _bridgeToOldDynamic = new Dictionary<DynamicBridgeTmpFont, BaseFont>();
        // 经典模式（bridgeDynamicToSdf=false）：DynamicFont.nativeFont 备份
        private readonly Dictionary<DynamicFont, object> _originalClassicNatives = new Dictionary<DynamicFont, object>();
        // 3D 场景 TMP_Text 备份
        private readonly Dictionary<UnityEngine.Object, object> _originalSceneTmpFonts = new Dictionary<UnityEngine.Object, object>();

        private BuiltTmpFontBundle _mainTmpBundle;
        private BuiltTmpFontBundle _titleTmpBundle;
        private object _classicOsFont;
        private object _sdkOverlayFont;
        private FontSettings _builtForSettings;
        private string _failedSignature;
        private bool _exportedFontNames;
        private bool _loggedRegistrySnapshot;

        // 一次性扫描闸门：Stage 树遍历与全场景 FindObjectsOfType 每帧代价高，
        // 完成一次后就置位，直到 Restore/Reset 或桥接集合变化才重扫。
        // FontManager.Clear() 重建字体后桥接集合会重新长出来，此时第三阶段必须再走一遍。
        private bool _stageSwapDone;
        private int _stageSwapBridgeCount = -1;
        private bool _sceneTmpDone;
        private double _nextSceneTmpRefresh;

        // 失败闸门：GetOSInstalledFontNames / 经典字体探测都是重量级同步调用，
        // 失败后不能每 0.5 秒重试，否则用户选中「不在 Unity 列表里」的用户目录字体时会持续卡顿。
        private bool _sdkOverlayAttempted;
        private bool _classicOsFontAttempted;

        // 注册表键快照缓存（键集合不变时复用，避免每轮分配）。
        private string[] _registryKeys;

        /// <summary>校验缓存的键快照与当前注册表键集合是否完全一致（逐键 exists，不分配）。</summary>
        private bool KeysStillMatch(Dictionary<string, BaseFont> registry)
        {
            if (_registryKeys == null || _registryKeys.Length != registry.Count) return false;
            for (int i = 0; i < _registryKeys.Length; i++)
            {
                if (!registry.ContainsKey(_registryKeys[i])) return false;
            }
            return true;
        }

        // Stage 遍历反射缓存
        private PropertyInfo _stageInstProp;
        private FieldInfo _containerChildrenField;
        private PropertyInfo _richTextFieldProp;
        private bool _stageReflectionInit;

        internal void FlushDynamicAtlases()
        {
            if (_mainTmpBundle != null || _titleTmpBundle != null)
            {
                _factory.FlushAtlasUploadQueue();
            }
        }

        /// <summary>
        /// 导出「Unity 系统字体 + 当前用户目录字体」的完整列表写入 data/FontMod-installed-fonts.json 供 Toys 字体页展示。
        /// </summary>
        internal void ExportInstalledFontNames(ModLogger log)
        {
            if (_exportedFontNames) return;
            try
            {
                Dictionary<string, BaseFont> registry = FontManager.sFontFactory;
                if (registry == null || registry.Count == 0) return;

                _factory.EnsureTypesResolved(null);
                Type fontType = _factory.FontType ?? ResolveFontTypeFromRegistry(registry);
                if (fontType == null) return;

                string[] osNames = GetInstalledFontNames(fontType);
                if (osNames == null || osNames.Length == 0) return;

                var merged = new List<string>(osNames);
                List<string> userNames = FontFileResolver.CollectUserFontFamilyNames(osNames);
                for (int i = 0; i < userNames.Count; i++) merged.Add(userNames[i]);

                string root = CesiumLoader.SDK.IO.Path.LoaderRootDirectory;
                if (string.IsNullOrEmpty(root)) return;
                string dir = Path.Combine(root, "data");
                Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.Append("[\n");
                for (int i = 0; i < merged.Count; i++)
                {
                    if (i > 0) sb.Append(",\n");
                    sb.Append("  ").Append(JsonString(merged[i]));
                }
                sb.Append("\n]\n");
                File.WriteAllText(Path.Combine(dir, "FontMod-installed-fonts.json"), sb.ToString(), Encoding.UTF8);
                _exportedFontNames = true;
                log.Info("已导出游戏与用户目录字体列表（系统 " + osNames.Length + " + 用户 " + userNames.Count + " = 共 " + merged.Count + " 项）");
            }
            catch (Exception e)
            {
                log.Warn("导出系统字体列表失败: " + e.Message);
            }
        }

        internal bool Apply(FontSettings settings, string modRoot, string loaderRoot, ModLogger log)
        {
            if (settings == null || !settings.HasTarget)
            {
                Restore(log);
                return false;
            }

            Dictionary<string, BaseFont> registry = FontManager.sFontFactory;
            if (registry == null || registry.Count == 0) return false;

            if (!_factory.EnsureTypesResolved(log)) return false;

            if (!_loggedRegistrySnapshot)
            {
                LogRegistrySnapshot(registry, log);
                _loggedRegistrySnapshot = true;
            }

            PruneStale(registry);

            string sig = settings.DescribeTarget();
            if (_builtForSettings != null && !_builtForSettings.SameAssetConfig(settings))
            {
                Restore(log);
            }

            if (string.Equals(_failedSignature, sig, StringComparison.Ordinal)) return false;

            bool needSdf = settings.ReplaceTmpFonts || settings.BridgeDynamicToSdf || settings.ReplaceSceneTmpText;

            // 1. 构建动态 SDF TMP_FontAsset（主字体 + 可选标题字体）
            if (needSdf)
            {
                if (_mainTmpBundle == null)
                {
                    object templateOrig = FindTemplateOriginalTmpAsset(registry);
                    ResolvedFontFile resolved = FontFileResolver.Resolve(
                        settings.FontPath,
                        settings.FontFamily,
                        settings.FontStyle,
                        modRoot,
                        loaderRoot,
                        _factory.FontType,
                        _factory.FontEngineType,
                        log);

                    if (resolved != null || !string.IsNullOrEmpty(settings.FontFamily))
                    {
                        _mainTmpBundle = _factory.BuildDynamicTmpFont(
                            resolved,
                            settings.FontFamily,
                            settings.FallbackFonts,
                            settings.SamplingPointSize,
                            settings.AtlasPadding,
                            settings.AtlasSize,
                            settings.TmpFontScale,
                            settings.NormalWeight,
                            settings.BoldWeight,
                            settings.WarmupGameCharset,
                            templateOrig,
                            log);
                    }

                    if (_mainTmpBundle != null)
                    {
                        log.Info("已构建动态主 TMP_FontAsset: " + _mainTmpBundle.SourceDescription
                            + ", 图集=" + _mainTmpBundle.AtlasWidth + "x" + _mainTmpBundle.AtlasHeight
                            + ", 总字形=" + _mainTmpBundle.WarmupGlyphCount
                            + ", 回退补字=" + _mainTmpBundle.FallbackFilledCount
                            + ", 仍缺=" + _mainTmpBundle.MissingGlyphCount);
                    }
                    else if (settings.ReplaceTmpFonts)
                    {
                        log.Warn("动态构建主 TMP_FontAsset 失败 (" + sig + ")，尝试仅使用旧版动态字体路径。");
                    }
                }

                if (settings.ApplyToAllFonts && !string.IsNullOrEmpty(settings.TitleFontPath) && _titleTmpBundle == null)
                {
                    object templateOrig = FindTemplateOriginalTmpAsset(registry);
                    ResolvedFontFile resolvedTitle = FontFileResolver.Resolve(
                        settings.TitleFontPath, "", "", modRoot, loaderRoot, _factory.FontType, _factory.FontEngineType, log);
                    if (resolvedTitle != null)
                    {
                        _titleTmpBundle = _factory.BuildDynamicTmpFont(
                            resolvedTitle,
                            "",
                            settings.FallbackFonts,
                            settings.SamplingPointSize,
                            settings.AtlasPadding,
                            Math.Min(settings.AtlasSize, 2048),
                            settings.TmpFontScale,
                            settings.NormalWeight,
                            settings.BoldWeight,
                            false,
                            templateOrig,
                            log);
                        if (_titleTmpBundle != null)
                        {
                            log.Info("已构建独立标题 TMP_FontAsset: " + _titleTmpBundle.SourceDescription
                                + ", 图集=" + _titleTmpBundle.AtlasWidth + "x" + _titleTmpBundle.AtlasHeight);
                        }
                    }
                }
            }

            bool useBridgeForDynamic = settings.BridgeDynamicToSdf && _mainTmpBundle != null;

            // 2. 若未桥接旧版字体（或 SDF 构建失败），走 v1.0.0 经典 OS 动态字体创建路径。
            //    该路径会枚举系统字体并创建探测字体，失败后置闸门，等配置真正变化再重试。
            if (!useBridgeForDynamic && _classicOsFont == null && !_classicOsFontAttempted
                && !string.IsNullOrEmpty(settings.FontFamily))
            {
                _classicOsFontAttempted = true;
                _classicOsFont = CreateValidatedClassicOsFont(
                    _factory.FontType, settings.FontFamily, settings.FallbackFonts, settings.CheckCoverage, log);
            }

            // 3. SDK 浮层字体（WidgetService.SetDefaultFont）：仅在 Unity 系统字体列表识别时推送。
            //    用户目录字体不在 Unity 的 GetOSInstalledFontNames 里，这里必然失败；
            //    置闸门避免每轮都做一次几十毫秒的系统字体枚举。
            if (_sdkOverlayFont == null && !_sdkOverlayAttempted && !string.IsNullOrEmpty(settings.FontFamily))
            {
                _sdkOverlayAttempted = true;
                _sdkOverlayFont = TryCreateSdkOverlayFont(_factory.FontType, settings.FontFamily, settings.FallbackFonts);
                if (_sdkOverlayFont != null) PushSdkFont(_sdkOverlayFont);
            }

            if (_mainTmpBundle == null && _classicOsFont == null)
            {
                log.Error("无法为目标「" + sig + "」构建可用字体，保持游戏默认字体。");
                _failedSignature = sig;
                return false;
            }

            _builtForSettings = settings;
            _failedSignature = null;

            int tmpApplied = 0;
            int tmpRestored = 0;
            int dynBridged = 0;
            int dynClassic = 0;
            int dynRestored = 0;
            int sceneApplied = 0;

            var appliedTmpNames = new List<string>();
            var appliedDynNames = new List<string>();

            // 键快照缓存：Apply 只替换字典里的值、不同时增删键，
            // 键集合没变时复用上一轮数组，省掉每 0.5 秒一次的 List<string> 分配。
            // 仅比对数量不足以证明键集合没变（同数目换名），因此逐键校验一次（不分配）。
            if (_registryKeys == null || !KeysStillMatch(registry))
            {
                _registryKeys = new string[registry.Count];
                registry.Keys.CopyTo(_registryKeys, 0);
            }

            for (int i = 0; i < _registryKeys.Length; i++)
            {
                string key = _registryKeys[i];
                BaseFont bf;
                if (!registry.TryGetValue(key, out bf) || bf == null) continue;

                bool isDecorative = IsDecorativeFont(key);

                // A. 已桥接过的 DynamicBridgeTmpFont
                DynamicBridgeTmpFont existingBridge = bf as DynamicBridgeTmpFont;
                if (existingBridge != null)
                {
                    bool shouldBridge = useBridgeForDynamic && (!isDecorative || settings.ApplyToAllFonts);
                    if (!shouldBridge)
                    {
                        if (RestoreSingleDynamicBridge(key, registry)) dynRestored++;
                    }
                    continue;
                }

                // B. 原生 FairyGUI.TMPFont / MyTMPFont
                if (IsNativeTmpFont(bf))
                {
                    bool shouldReplace = settings.ReplaceTmpFonts && _mainTmpBundle != null
                        && (!isDecorative || settings.ApplyToAllFonts);
                    if (!shouldReplace)
                    {
                        if (RestoreSingleTmpFont(bf, log)) tmpRestored++;
                        continue;
                    }

                    object desiredAsset = (isDecorative && _titleTmpBundle != null)
                        ? _titleTmpBundle.TmpFontAsset
                        : _mainTmpBundle.TmpFontAsset;

                    object currentAsset = GetTmpFontAsset(bf);
                    if (ReferenceEquals(currentAsset, desiredAsset)) continue;

                    if (!_originalTmpAssets.ContainsKey(bf) && currentAsset != null)
                    {
                        _originalTmpAssets[bf] = currentAsset;
                    }

                    if (SetTmpFontAsset(bf, desiredAsset, log))
                    {
                        bf.version++;
                        tmpApplied++;
                        appliedTmpNames.Add(key);
                    }
                    continue;
                }

                // C. 旧版 FairyGUI.DynamicFont / MyDynamicFont
                DynamicFont df = bf as DynamicFont;
                if (df != null)
                {
                    if (useBridgeForDynamic)
                    {
                        if (isDecorative && !settings.ApplyToAllFonts) continue;

                        object desiredAsset = (isDecorative && _titleTmpBundle != null)
                            ? _titleTmpBundle.TmpFontAsset
                            : _mainTmpBundle.TmpFontAsset;

                        try
                        {
                            DynamicBridgeTmpFont bridge = DynamicBridgeTmpFont.Create(key, desiredAsset);
                            if (bridge != null)
                            {
                                _originalDynamicByKey[key] = df;
                                _bridgeByKey[key] = bridge;
                                _oldDynamicToBridge[df] = bridge;
                                _bridgeToOldDynamic[bridge] = df;

                                registry[key] = bridge;
                                dynBridged++;
                                appliedDynNames.Add(key);
                            }
                        }
                        catch (Exception e)
                        {
                            log.Warn("桥接旧版字体「" + key + "」为 SDF 失败: " + e.Message);
                        }
                    }
                    else if (_classicOsFont != null)
                    {
                        if (!settings.ApplyToAllFonts && !IsDefaultFont(key)) continue;
                        object currentNative = GetDynamicNativeFont(df);
                        if (ReferenceEquals(currentNative, _classicOsFont)) continue;
                        if (!_originalClassicNatives.ContainsKey(df) && currentNative != null)
                        {
                            _originalClassicNatives[df] = currentNative;
                        }
                        if (SetDynamicNativeFont(df, _classicOsFont, log))
                        {
                            dynClassic++;
                            appliedDynNames.Add(key);
                        }
                    }
                }
            }

            // D. 热切换 Stage.inst 上所有已存在的旧版 TextField / RichTextField。
            //    这是一次全 FairyGUI 显示树的反射遍历，只在桥接集合发生变化时做一次；
            //    之后再创建的新控件会在构造时直接取到 registry 里的桥接字体，无需重扫。
            int stageSwapped = 0;
            if (_oldDynamicToBridge.Count > 0
                && (!_stageSwapDone || _stageSwapBridgeCount != _oldDynamicToBridge.Count))
            {
                stageSwapped = SwapStageTextFields(_oldDynamicToBridge, settings.ApplyToAllFonts);
                _stageSwapDone = true;
                _stageSwapBridgeCount = _oldDynamicToBridge.Count;
            }

            // E. 替换 3D 场景 TMPro.TMP_Text。
            //    FindObjectsOfType 是主线程全场景扫描且每次分配数组，首次立即补一轮，
            //    之后按 SceneTmpRefreshSeconds 低频补扫新实例化对象。
            if (settings.ReplaceSceneTmpText && _mainTmpBundle != null)
            {
                if (!_sceneTmpDone || NowSeconds() >= _nextSceneTmpRefresh)
                {
                    sceneApplied = ApplySceneTmpTexts(_mainTmpBundle.TmpFontAsset);
                    _sceneTmpDone = true;
                    _nextSceneTmpRefresh = NowSeconds() + SceneTmpRefreshSeconds;
                }
            }

            if (tmpApplied > 0 || tmpRestored > 0 || dynBridged > 0 || dynClassic > 0 || dynRestored > 0 || stageSwapped > 0 || sceneApplied > 0)
            {
                if (_classicOsFont != null) WarmupClassicFont(_classicOsFont);
                _factory.FlushAtlasUploadQueue();
                RequestTextRebuild();
                log.Info("系统字体同步完成: "
                    + "TMP替换=" + tmpApplied + (appliedTmpNames.Count > 0 ? (" [" + string.Join(", ", appliedTmpNames.ToArray()) + "]") : "")
                    + (dynBridged > 0 ? (", 旧版桥接SDF=" + dynBridged + " [" + string.Join(", ", appliedDynNames.ToArray()) + "]") : "")
                    + (dynClassic > 0 ? (", 旧版替换=" + dynClassic + " [" + string.Join(", ", appliedDynNames.ToArray()) + "]") : "")
                    + (stageSwapped > 0 ? (", 已有文本热切换=" + stageSwapped) : "")
                    + (tmpRestored + dynRestored > 0 ? (", 还原=" + (tmpRestored + dynRestored)) : "")
                    + (sceneApplied > 0 ? (", 场景TMP=" + sceneApplied) : ""));
                return true;
            }
            return false;
        }

        internal void Restore(ModLogger log)
        {
            Dictionary<string, BaseFont> registry = FontManager.sFontFactory;

            // 复位一次性扫描闸门：下次重新 Apply 时 Stage 树与场景文本需要再走一遍。
            _stageSwapDone = false;
            _stageSwapBridgeCount = -1;
            _sceneTmpDone = false;
            _nextSceneTmpRefresh = 0;
            _registryKeys = null;

            // 1. 恢复原生 TMPFont
            int tmpRestored = 0;
            foreach (KeyValuePair<BaseFont, object> pair in _originalTmpAssets)
            {
                BaseFont bf = pair.Key;
                object orig = pair.Value;
                if (bf == null || orig == null) continue;
                if (SetTmpFontAsset(bf, orig, log))
                {
                    bf.version++;
                    tmpRestored++;
                }
            }
            _originalTmpAssets.Clear();

            // 2. 恢复桥接的旧版 DynamicFont 与 Stage.inst 文本
            int stageRestored = 0;
            if (_bridgeToOldDynamic.Count > 0)
            {
                stageRestored = RestoreStageTextFields(_bridgeToOldDynamic);
            }

            int dynRestored = 0;
            if (registry != null)
            {
                var keys = new List<string>(_originalDynamicByKey.Keys);
                for (int i = 0; i < keys.Count; i++)
                {
                    if (RestoreSingleDynamicBridge(keys[i], registry)) dynRestored++;
                }
            }
            foreach (KeyValuePair<string, DynamicBridgeTmpFont> pair in _bridgeByKey)
            {
                if (pair.Value != null) pair.Value.Dispose();
            }
            _originalDynamicByKey.Clear();
            _bridgeByKey.Clear();
            _oldDynamicToBridge.Clear();
            _bridgeToOldDynamic.Clear();

            // 3. 恢复经典模式 DynamicFont.nativeFont
            int classicRestored = 0;
            foreach (KeyValuePair<DynamicFont, object> pair in _originalClassicNatives)
            {
                if (pair.Key == null || pair.Value == null) continue;
                if (SetDynamicNativeFont(pair.Key, pair.Value, log))
                {
                    WarmupClassicFont(pair.Value);
                    classicRestored++;
                }
            }
            _originalClassicNatives.Clear();

            // 4. 恢复 3D 场景 TMP_Text
            int sceneRestored = RestoreSceneTmpTexts();

            if (tmpRestored > 0 || dynRestored > 0 || classicRestored > 0 || stageRestored > 0 || sceneRestored > 0)
            {
                RequestTextRebuild();
                if (log != null)
                {
                    log.Info("已恢复游戏默认字体: TMP=" + tmpRestored + ", 旧版=" + (dynRestored + classicRestored)
                        + ", 界面文本=" + stageRestored + ", 场景TMP=" + sceneRestored);
                }
            }

            DestroyBuiltAssets();
            PushSdkFont(null);
            _builtForSettings = null;
            _failedSignature = null;

            // 配置变化时清掉失败闸门，让新的目标字体有机会重试一次。
            _sdkOverlayAttempted = false;
            _classicOsFontAttempted = false;
        }

        internal void Reset(ModLogger log)
        {
            Restore(log);
        }

        private void DestroyBuiltAssets()
        {
            if (_mainTmpBundle != null)
            {
                _mainTmpBundle.Destroy();
                _mainTmpBundle = null;
            }
            if (_titleTmpBundle != null)
            {
                _titleTmpBundle.Destroy();
                _titleTmpBundle = null;
            }
            if (_classicOsFont != null)
            {
                DestroyObject(_classicOsFont);
                _classicOsFont = null;
            }
            if (_sdkOverlayFont != null)
            {
                DestroyObject(_sdkOverlayFont);
                _sdkOverlayFont = null;
            }
        }

        private object TryCreateSdkOverlayFont(Type fontType, string family, string fallbackCsv)
        {
            if (fontType == null || string.IsNullOrEmpty(family)) return null;
            try
            {
                string[] installed = GetInstalledFontNames(fontType);
                if (installed != null && installed.Length > 0 && !FontNames.Contains(installed, family))
                {
                    return null;
                }
                return _factory.CreateUnityFontFromOs(family, fallbackCsv, 32);
            }
            catch
            {
                return null;
            }
        }

        private object CreateValidatedClassicOsFont(Type fontType, string family, string fallbackCsv, bool checkCoverage, ModLogger log)
        {
            if (fontType == null || string.IsNullOrEmpty(family)) return null;

            string[] installed = GetInstalledFontNames(fontType);
            if (installed != null && installed.Length > 0 && !FontNames.Contains(installed, family))
            {
                log.Warn("旧版动态字体路径无法识别「" + family + "」（可能仅安装在用户目录）。");
                return null;
            }

            if (!ProbeClassicFont(fontType, family, log)) return null;

            object font = _factory.CreateUnityFontFromOs(family, fallbackCsv, 64);
            if (font == null) return null;

            if (checkCoverage)
            {
                string missingSample;
                int missing = CountMissingClassic(font, out missingSample);
                if (missing > MaxMissingGlyphs)
                {
                    log.Error("字体「" + family + "」缺少 " + missing + " 个游戏用字（" + missingSample + " 等），已跳过旧版替换。");
                    DestroyObject(font);
                    return null;
                }
            }
            return font;
        }

        private bool ProbeClassicFont(Type fontType, string family, ModLogger log)
        {
            object probe = null;
            try
            {
                probe = _factory.CreateUnityFontFromOs(family, null, 64);
                if (probe == null) return false;

                MethodInfo hasChar = probe.GetType().GetMethod(
                    "HasCharacter", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(char) }, null);
                if (hasChar == null) return true;

                int missing = 0;
                for (int i = 0; i < CoreChineseSample.Length; i++)
                {
                    bool has = false;
                    try { has = (bool)hasChar.Invoke(probe, new object[] { CoreChineseSample[i] }); }
                    catch { has = false; }
                    if (!has) missing++;
                }
                if (missing > MaxProbeMissing)
                {
                    if (log != null) log.Warn("旧版字体探测「" + family + "」缺少常用中文 " + missing + " 个。");
                    return false;
                }
                return true;
            }
            finally
            {
                DestroyObject(probe);
            }
        }

        private static int CountMissingClassic(object font, out string sample)
        {
            sample = "";
            try
            {
                MethodInfo method = font.GetType().GetMethod(
                    "HasCharacter", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(char) }, null);
                if (method == null) return 0;

                string text = GameCharset.Text;
                var sampleChars = new List<char>(24);
                int missing = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    bool has;
                    try { has = (bool)method.Invoke(font, new object[] { ch }); }
                    catch { return 0; }
                    if (has) continue;
                    missing++;
                    if (sampleChars.Count < 24 && !sampleChars.Contains(ch)) sampleChars.Add(ch);
                }
                sample = new string(sampleChars.ToArray());
                return missing;
            }
            catch
            {
                return 0;
            }
        }

        private bool RestoreSingleDynamicBridge(string key, Dictionary<string, BaseFont> registry)
        {
            BaseFont origDyn;
            if (!_originalDynamicByKey.TryGetValue(key, out origDyn) || origDyn == null) return false;

            DynamicBridgeTmpFont bridge;
            _bridgeByKey.TryGetValue(key, out bridge);

            if (registry != null)
            {
                BaseFont current;
                if (registry.TryGetValue(key, out current) && ReferenceEquals(current, bridge))
                {
                    registry[key] = origDyn;
                }
            }

            if (bridge != null)
            {
                var singleRestore = new Dictionary<DynamicBridgeTmpFont, BaseFont>();
                singleRestore[bridge] = origDyn;
                RestoreStageTextFields(singleRestore);
                _bridgeToOldDynamic.Remove(bridge);
                bridge.Dispose();
            }

            _oldDynamicToBridge.Remove(origDyn);
            _originalDynamicByKey.Remove(key);
            _bridgeByKey.Remove(key);
            origDyn.version++;
            return true;
        }

        private bool RestoreSingleTmpFont(BaseFont bf, ModLogger log)
        {
            object orig;
            if (!_originalTmpAssets.TryGetValue(bf, out orig) || orig == null) return false;
            _originalTmpAssets.Remove(bf);
            if (SetTmpFontAsset(bf, orig, log))
            {
                bf.version++;
                return true;
            }
            return false;
        }

        private void EnsureStageReflection()
        {
            if (_stageReflectionInit) return;
            _stageReflectionInit = true;
            try
            {
                Assembly fgui = typeof(BaseFont).Assembly;
                Type stageType = fgui.GetType("FairyGUI.Stage");
                if (stageType != null) _stageInstProp = stageType.GetProperty("inst", BindingFlags.Public | BindingFlags.Static);
                Type containerType = fgui.GetType("FairyGUI.Container");
                if (containerType != null) _containerChildrenField = containerType.GetField("_children", BindingFlags.NonPublic | BindingFlags.Instance);
                Type richType = fgui.GetType("FairyGUI.RichTextField");
                if (richType != null) _richTextFieldProp = richType.GetProperty("textField", BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
            }
        }

        private int SwapStageTextFields(Dictionary<BaseFont, DynamicBridgeTmpFont> oldToBridge, bool allowDecorativeFallback)
        {
            EnsureStageReflection();
            if (_stageInstProp == null || _containerChildrenField == null || TextFieldFontField == null) return 0;

            object stage = null;
            try { stage = _stageInstProp.GetValue(null, null); }
            catch { return 0; }
            if (stage == null) return 0;

            DynamicBridgeTmpFont defaultBridge = null;
            if (!_bridgeByKey.TryGetValue("JingNanBoBoHei", out defaultBridge))
            {
                foreach (KeyValuePair<string, DynamicBridgeTmpFont> kv in _bridgeByKey)
                {
                    defaultBridge = kv.Value;
                    break;
                }
            }

            int swapped = 0;
            var stack = new Stack<object>();
            stack.Push(stage);

            while (stack.Count > 0)
            {
                object node = stack.Pop();
                if (node == null) continue;

                TextField tf = node as TextField;
                if (tf != null)
                {
                    if (TrySwapSingleTextField(tf, oldToBridge, defaultBridge, allowDecorativeFallback)) swapped++;
                    continue;
                }

                if (_richTextFieldProp != null && _richTextFieldProp.DeclaringType.IsInstanceOfType(node))
                {
                    TextField innerTf = _richTextFieldProp.GetValue(node, null) as TextField;
                    if (innerTf != null && TrySwapSingleTextField(innerTf, oldToBridge, defaultBridge, allowDecorativeFallback)) swapped++;
                }

                if (_containerChildrenField.DeclaringType.IsInstanceOfType(node))
                {
                    IList children = _containerChildrenField.GetValue(node) as IList;
                    if (children != null)
                    {
                        for (int i = 0; i < children.Count; i++)
                        {
                            if (children[i] != null) stack.Push(children[i]);
                        }
                    }
                }
            }
            return swapped;
        }

        private bool TrySwapSingleTextField(
            TextField tf,
            Dictionary<BaseFont, DynamicBridgeTmpFont> oldToBridge,
            DynamicBridgeTmpFont defaultBridge,
            bool allowDecorativeFallback)
        {
            BaseFont curFont = TextFieldFontField.GetValue(tf) as BaseFont;
            if (curFont == null || curFont is DynamicBridgeTmpFont || IsNativeTmpFont(curFont)) return false;

            DynamicBridgeTmpFont bridge;
            if (!oldToBridge.TryGetValue(curFont, out bridge))
            {
                if (curFont is DynamicFont && defaultBridge != null && (allowDecorativeFallback || !IsDecorativeFont(curFont.name)))
                {
                    bridge = defaultBridge;
                }
                else
                {
                    return false;
                }
            }

            TextFieldFontField.SetValue(tf, bridge);
            if (TextFieldFontVersionField != null) TextFieldFontVersionField.SetValue(tf, -1);
            if (TextFieldTextChangedField != null) TextFieldTextChangedField.SetValue(tf, true);
            return true;
        }

        private int RestoreStageTextFields(Dictionary<DynamicBridgeTmpFont, BaseFont> bridgeToOld)
        {
            EnsureStageReflection();
            if (_stageInstProp == null || _containerChildrenField == null || TextFieldFontField == null) return 0;

            object stage = null;
            try { stage = _stageInstProp.GetValue(null, null); }
            catch { return 0; }
            if (stage == null) return 0;

            int restored = 0;
            var stack = new Stack<object>();
            stack.Push(stage);

            while (stack.Count > 0)
            {
                object node = stack.Pop();
                if (node == null) continue;

                TextField tf = node as TextField;
                if (tf != null)
                {
                    if (TryRestoreSingleTextField(tf, bridgeToOld)) restored++;
                    continue;
                }

                if (_richTextFieldProp != null && _richTextFieldProp.DeclaringType.IsInstanceOfType(node))
                {
                    TextField innerTf = _richTextFieldProp.GetValue(node, null) as TextField;
                    if (innerTf != null && TryRestoreSingleTextField(innerTf, bridgeToOld)) restored++;
                }

                if (_containerChildrenField.DeclaringType.IsInstanceOfType(node))
                {
                    IList children = _containerChildrenField.GetValue(node) as IList;
                    if (children != null)
                    {
                        for (int i = 0; i < children.Count; i++)
                        {
                            if (children[i] != null) stack.Push(children[i]);
                        }
                    }
                }
            }
            return restored;
        }

        private static bool TryRestoreSingleTextField(TextField tf, Dictionary<DynamicBridgeTmpFont, BaseFont> bridgeToOld)
        {
            DynamicBridgeTmpFont bridge = TextFieldFontField.GetValue(tf) as DynamicBridgeTmpFont;
            if (bridge == null) return false;

            BaseFont orig;
            if (!bridgeToOld.TryGetValue(bridge, out orig) || orig == null) return false;

            TextFieldFontField.SetValue(tf, orig);
            if (TextFieldFontVersionField != null) TextFieldFontVersionField.SetValue(tf, -1);
            if (TextFieldTextChangedField != null) TextFieldTextChangedField.SetValue(tf, true);
            return true;
        }

        private int ApplySceneTmpTexts(object desiredAsset)
        {
            if (_factory.TmpTextType == null || desiredAsset == null) return 0;
            int count = 0;
            try
            {
                UnityEngine.Object[] objs = UnityEngine.Object.FindObjectsOfType(_factory.TmpTextType);
                if (objs == null || objs.Length == 0) return 0;

                PropertyInfo fontProp = _factory.TmpTextType.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo forceUpdate = _factory.TmpTextType.GetMethod(
                    "ForceMeshUpdate", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool), typeof(bool) }, null);
                if (fontProp == null) return 0;

                for (int i = 0; i < objs.Length; i++)
                {
                    UnityEngine.Object comp = objs[i];
                    if (comp == null) continue;
                    object cur = fontProp.GetValue(comp, null);
                    if (ReferenceEquals(cur, desiredAsset)) continue;
                    if (!_originalSceneTmpFonts.ContainsKey(comp) && cur != null)
                    {
                        _originalSceneTmpFonts[comp] = cur;
                    }
                    fontProp.SetValue(comp, desiredAsset, null);
                    if (forceUpdate != null) forceUpdate.Invoke(comp, new object[] { true, true });
                    count++;
                }
            }
            catch
            {
            }
            return count;
        }

        private int RestoreSceneTmpTexts()
        {
            int count = 0;
            if (_factory.TmpTextType != null)
            {
                PropertyInfo fontProp = _factory.TmpTextType.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo forceUpdate = _factory.TmpTextType.GetMethod(
                    "ForceMeshUpdate", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool), typeof(bool) }, null);
                if (fontProp != null)
                {
                    foreach (KeyValuePair<UnityEngine.Object, object> pair in _originalSceneTmpFonts)
                    {
                        if (pair.Key == null || pair.Value == null) continue;
                        try
                        {
                            fontProp.SetValue(pair.Key, pair.Value, null);
                            if (forceUpdate != null) forceUpdate.Invoke(pair.Key, new object[] { true, true });
                            count++;
                        }
                        catch
                        {
                        }
                    }
                }
            }
            _originalSceneTmpFonts.Clear();
            return count;
        }

        private void PruneStale(Dictionary<string, BaseFont> registry)
        {
            // 稳态下各跟踪集合都已清空或稳定，直接返回，避免每轮为四个列表分配内存。
            if (_originalTmpAssets.Count == 0 && _originalClassicNatives.Count == 0
                && _bridgeByKey.Count == 0 && _originalSceneTmpFonts.Count == 0)
            {
                return;
            }

            var staleTmp = new List<BaseFont>();
            foreach (KeyValuePair<BaseFont, object> pair in _originalTmpAssets)
            {
                if (!ContainsValue(registry, pair.Key)) staleTmp.Add(pair.Key);
            }
            for (int i = 0; i < staleTmp.Count; i++) _originalTmpAssets.Remove(staleTmp[i]);

            var staleClassic = new List<DynamicFont>();
            foreach (KeyValuePair<DynamicFont, object> pair in _originalClassicNatives)
            {
                if (!ContainsValue(registry, pair.Key)) staleClassic.Add(pair.Key);
            }
            for (int i = 0; i < staleClassic.Count; i++) _originalClassicNatives.Remove(staleClassic[i]);

            var staleKeys = new List<string>();
            foreach (KeyValuePair<string, DynamicBridgeTmpFont> pair in _bridgeByKey)
            {
                BaseFont cur;
                if (!registry.TryGetValue(pair.Key, out cur) || !ReferenceEquals(cur, pair.Value))
                {
                    staleKeys.Add(pair.Key);
                }
            }
            for (int i = 0; i < staleKeys.Count; i++)
            {
                string k = staleKeys[i];
                DynamicBridgeTmpFont b;
                if (_bridgeByKey.TryGetValue(k, out b))
                {
                    _bridgeToOldDynamic.Remove(b);
                    _bridgeByKey.Remove(k);
                }
                BaseFont oldDyn;
                if (_originalDynamicByKey.TryGetValue(k, out oldDyn))
                {
                    _oldDynamicToBridge.Remove(oldDyn);
                    _originalDynamicByKey.Remove(k);
                }
            }

            var staleScene = new List<UnityEngine.Object>();
            foreach (KeyValuePair<UnityEngine.Object, object> pair in _originalSceneTmpFonts)
            {
                if (pair.Key == null) staleScene.Add(pair.Key);
            }
            for (int i = 0; i < staleScene.Count; i++) _originalSceneTmpFonts.Remove(staleScene[i]);
        }

        private static bool ContainsValue(Dictionary<string, BaseFont> registry, BaseFont target)
        {
            foreach (KeyValuePair<string, BaseFont> kv in registry)
            {
                if (ReferenceEquals(kv.Value, target)) return true;
            }
            return false;
        }

        private object FindTemplateOriginalTmpAsset(Dictionary<string, BaseFont> registry)
        {
            foreach (KeyValuePair<BaseFont, object> kv in _originalTmpAssets)
            {
                if (kv.Value != null) return kv.Value;
            }
            foreach (KeyValuePair<string, BaseFont> kv in registry)
            {
                if (!IsNativeTmpFont(kv.Value)) continue;
                object asset = GetTmpFontAsset(kv.Value);
                if (asset != null && kv.Key.IndexOf("JingNan", StringComparison.OrdinalIgnoreCase) >= 0) return asset;
            }
            foreach (KeyValuePair<string, BaseFont> kv in registry)
            {
                if (!IsNativeTmpFont(kv.Value)) continue;
                object asset = GetTmpFontAsset(kv.Value);
                if (asset != null) return asset;
            }
            return null;
        }

        private static Type ResolveFontTypeFromRegistry(Dictionary<string, BaseFont> registry)
        {
            foreach (KeyValuePair<string, BaseFont> pair in registry)
            {
                DynamicFont df = pair.Value as DynamicFont;
                if (df == null) continue;
                object native = GetDynamicNativeFont(df);
                if (native != null) return native.GetType();
            }
            return null;
        }

        private static string[] GetInstalledFontNames(Type fontType)
        {
            try
            {
                MethodInfo method = fontType.GetMethod(
                    "GetOSInstalledFontNames", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                return method != null ? method.Invoke(null, null) as string[] : null;
            }
            catch
            {
                return null;
            }
        }

        private static void LogRegistrySnapshot(Dictionary<string, BaseFont> registry, ModLogger log)
        {
            if (log == null) return;
            var items = new List<string>();
            foreach (KeyValuePair<string, BaseFont> kv in registry)
            {
                string typeName = kv.Value == null ? "null" : kv.Value.GetType().Name;
                string extra = "";
                if (IsNativeTmpFont(kv.Value))
                {
                    object asset = GetTmpFontAsset(kv.Value);
                    UnityEngine.Object uo = asset as UnityEngine.Object;
                    if (uo != null) extra = "(" + uo.name + ")";
                }
                items.Add(kv.Key + ":" + typeName + extra);
            }
            log.Info("检测到 FairyGUI 已注册字体 (" + items.Count + " 项): " + string.Join(", ", items.ToArray()));
        }

        private static bool IsDefaultFont(string name)
        {
            string defaultFont = UIConfig.defaultFont;
            return !string.IsNullOrEmpty(defaultFont)
                && string.Equals(name, defaultFont, StringComparison.Ordinal);
        }

        private static bool IsNativeTmpFont(BaseFont bf)
        {
            if (bf == null || bf is DynamicBridgeTmpFont) return false;
            Type t = bf.GetType();
            while (t != null && t != typeof(object))
            {
                if (string.Equals(t.Name, "TMPFont", StringComparison.Ordinal)) return true;
                t = t.BaseType;
            }
            return false;
        }

        private static bool IsDecorativeFont(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (DecorativeFontNames.Contains(key)) return true;
            return key.IndexOf("SHOWG", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("Impact", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("hp02", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("dgo", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("Vonwaon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object GetTmpFontAsset(BaseFont tmpFont)
        {
            try
            {
                PropertyInfo p = tmpFont.GetType().GetProperty("fontAsset", BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return p.GetValue(tmpFont, null);
            }
            catch
            {
            }
            return null;
        }

        private static bool SetTmpFontAsset(BaseFont tmpFont, object asset, ModLogger log)
        {
            try
            {
                PropertyInfo p = tmpFont.GetType().GetProperty("fontAsset", BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return false;
                p.SetValue(tmpFont, asset, null);
                return true;
            }
            catch (TargetInvocationException tie)
            {
                if (log != null) log.Warn("设置 TMPFont.fontAsset 失败 (" + tmpFont.name + "): "
                    + (tie.InnerException != null ? tie.InnerException.Message : tie.Message));
                return false;
            }
            catch (Exception e)
            {
                if (log != null) log.Warn("设置 TMPFont.fontAsset 失败 (" + tmpFont.name + "): " + e.Message);
                return false;
            }
        }

        private static object GetDynamicNativeFont(DynamicFont df)
        {
            try { return DynamicNativeFontProp != null ? DynamicNativeFontProp.GetValue(df, null) : null; }
            catch { return null; }
        }

        private static bool SetDynamicNativeFont(DynamicFont df, object nativeFont, ModLogger log)
        {
            try
            {
                if (DynamicNativeFontProp == null) return false;
                DynamicNativeFontProp.SetValue(df, nativeFont, null);
                return true;
            }
            catch (Exception e)
            {
                if (log != null) log.Warn("设置 DynamicFont.nativeFont 失败 (" + df.name + "): " + e.Message);
                return false;
            }
        }

        private static void WarmupClassicFont(object font)
        {
            if (font == null) return;
            try
            {
                MethodInfo method = null;
                MethodInfo[] methods = font.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < methods.Length; i++)
                {
                    ParameterInfo[] parameters = methods[i].GetParameters();
                    if (!string.Equals(methods[i].Name, "RequestCharactersInTexture", StringComparison.Ordinal)) continue;
                    if (parameters.Length < 2) continue;
                    if (parameters[0].ParameterType != typeof(string) || parameters[1].ParameterType != typeof(int)) continue;
                    method = methods[i];
                    break;
                }
                if (method == null) return;
                ParameterInfo[] ps = method.GetParameters();
                object[] args = new object[ps.Length];
                args[0] = WarmupText;
                args[1] = 32;
                for (int i = 2; i < ps.Length; i++)
                {
                    args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                }
                method.Invoke(font, args);
            }
            catch
            {
            }
        }

        private static void RequestTextRebuild()
        {
            try
            {
                if (BaseFontTextRebuildFlagField != null) BaseFontTextRebuildFlagField.SetValue(null, true);
            }
            catch
            {
            }
        }

        private static void PushSdkFont(object font)
        {
            try { WidgetService.SetDefaultFont(font); }
            catch { }
        }

        /// <summary>不受变速 hook 影响的时间基准（与 FontController 保持一致）。</summary>
        private static double NowSeconds()
        {
            return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }

        private static void DestroyObject(object obj)
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

        private static string JsonString(string value)
        {
            var sb = new StringBuilder("\"");
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
