using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.UserInterface;
using FairyGUI;

namespace FontMod
{
    /// <summary>
    /// 把 FairyGUI 里已注册的旧版动态字体（<see cref="DynamicFont"/>）换成系统字体。
    ///
    /// 游戏登录时 <c>UIManager.AsyncLoadFonts</c> 会按 <c>Font.bin</c> 注册一批
    /// <see cref="DynamicFont"/>（默认正文用 <c>JingNanBoBoHei</c>），它们各自持有一个
    /// <c>UnityEngine.Font</c>。这里只替换 <c>nativeFont</c>，注册名不变，所以所有引用
    /// 该名字的文字都会自动用上新字体；赋值会重建纹理并触发 FairyGUI 的整页文字重排。
    ///
    /// <c>UnityEngine.Font</c> 在 UnityEngine.TextRenderingModule 里，游戏引用程序集只带
    /// CoreModule，所以字体对象全程用反射（静态方法从已有 <c>nativeFont</c> 的运行时类型上取）。
    /// </summary>
    internal sealed class FontReplacement
    {
        private const int MaxMissingGlyphs = 25;

        private const string WarmupText = "永和九年，岁在癸丑。星币筹码技能攻击防御0123456789ABCabc";

        /// <summary>
        /// 单名字探测用的常用中文。多名字 Font 在名字都不存在时也会返回非空对象，
        /// 且 HasCharacter 会被回退字体（雅黑）骗过去；必须用单名字 Font 才能分辨
        /// “用户选的字体真生效”与“被悄悄回退成雅黑”。
        /// </summary>
        private const string CoreChineseSample =
            "中文的一是在不了有和人这大上个国我以要他时来用们生到作地于出就分对成会可主发年动同工也能下过子说产种";

        /// <summary>探测时最多允许缺几个常用字；超过就认定这个字体名在游戏里不可用于中文。</summary>
        private const int MaxProbeMissing = 2;

        private static readonly PropertyInfo NativeFontProperty =
            typeof(DynamicFont).GetProperty("nativeFont", BindingFlags.Public | BindingFlags.Instance);

        private readonly Dictionary<DynamicFont, object> _originals = new Dictionary<DynamicFont, object>();
        private object _replacement;
        private string _family;
        private string _failedFamily;
        private bool _exportedFontNames;

        internal string AppliedFamily { get { return _family; } }
        internal int ReplacedCount { get { return _originals.Count; } }

        /// <summary>
        /// 套用字体。<paramref name="applyToAll"/> 为 false 时只换默认正文（UC 的 defaultFont）。
        /// 返回是否实际替换了字体；字体尚未注册、名字无效或加载失败时返回 false（保持原样）。
        /// </summary>
        internal bool Apply(string family, string fallbackCsv, bool applyToAll, bool checkCoverage, ModLogger log)
        {
            if (string.IsNullOrEmpty(family))
            {
                Restore(log);
                return false;
            }

            Dictionary<string, BaseFont> registry = FontManager.sFontFactory;
            if (registry == null || registry.Count == 0)
            {
                // 登录界面还没走到 AsyncLoadFonts；由控制器下一次轮询再试。
                return false;
            }

            Type fontType = ResolveFontType(registry);
            if (fontType == null) return false;

            object font = GetOrCreate(fontType, family, fallbackCsv, checkCoverage, log);
            if (font == null) return false;

            PruneStale(registry);

            int applied = 0;
            foreach (KeyValuePair<string, BaseFont> pair in registry)
            {
                DynamicFont dynamicFont = pair.Value as DynamicFont;
                if (dynamicFont == null) continue;   // TMP 字体不在本模组范围内
                if (!applyToAll && !IsDefaultFont(pair.Key)) continue;

                object native = GetNativeFont(dynamicFont);
                if (ReferenceEquals(native, font)) continue;

                if (!_originals.ContainsKey(dynamicFont))
                {
                    _originals[dynamicFont] = native;
                }

                try
                {
                    NativeFontProperty.SetValue(dynamicFont, font, null);
                    applied++;
                }
                catch (Exception e)
                {
                    log.Warn("替换字体「" + pair.Key + "」失败: " + e.Message);
                }
            }

            if (applied > 0)
            {
                Warmup(font);
                RequestTextRebuild();
                PushSdkFont(font);
                log.Info("已把 " + applied + " 个界面字体替换为「" + family
                    + "」（已通过单名字校验，中文由该字体渲染）");
            }
            return applied > 0;
        }

        /// <summary>恢复成游戏原字体并销毁临时字体。配置清空、停用或卸载时调用。</summary>
        internal void Restore(ModLogger log)
        {
            int restored = 0;
            foreach (KeyValuePair<DynamicFont, object> pair in _originals)
            {
                DynamicFont dynamicFont = pair.Key;
                object original = pair.Value;
                if (dynamicFont == null || original == null) continue;

                try
                {
                    NativeFontProperty.SetValue(dynamicFont, original, null);
                    Warmup(original);
                    restored++;
                }
                catch (Exception e)
                {
                    log.Warn("恢复字体失败: " + e.Message);
                }
            }

            _originals.Clear();

            if (restored > 0)
            {
                RequestTextRebuild();
                log.Info("已恢复游戏默认字体（" + restored + " 项）");
            }

            DestroyReplacement();
            PushSdkFont(null);
            _failedFamily = null;
        }

        /// <summary>换字体前先还原一次，强制下次重新创建。</summary>
        internal void Reset(ModLogger log)
        {
            Restore(log);
        }

        private object GetOrCreate(Type fontType, string family, string fallbackCsv, bool checkCoverage, ModLogger log)
        {
            if (_replacement != null && string.Equals(_family, family, StringComparison.Ordinal))
            {
                return _replacement;
            }

            DestroyReplacement();

            // 同一个字体名已经失败过就别每次轮询都重试/刷日志；改配置后自然会重试。
            if (string.Equals(_failedFamily, family, StringComparison.Ordinal)) return null;

            string[] candidates = FontNames.BuildCandidates(family, fallbackCsv);

            // 硬判据：名字必须在游戏自己枚举的 OS 字体列表里。
            // 这是唯一可靠的判断——Unity 的 CreateDynamicFontFromOSFont 对不存在的名字
            // 也会返回一个非空 Font，且 HasCharacter 会 fallback 到系统默认字体返回 true，
            // 所以单名字探测（以及多名字后的 HasCharacter）都无法分辨“真字体”和“回退”。
            // 列表读不到（null/空）时不拦，交给后面的探测与多名字加载。
            string[] installed = GetInstalledFontNames(fontType);
            if (installed != null && installed.Length > 0 && !FontNames.Contains(installed, family))
            {
                log.Error("字体「" + family + "」不在游戏可识别的系统字体列表里，已保持游戏默认字体。"
                    + "请在 Toys「自定义 → 字体」里重新选一个列表中的字体。");
                _failedFamily = family;
                return null;
            }

            // 辅助校验：名字在列表里也确认一次能渲染常用中文（例如纯拉丁字体要拦掉）。
            if (!ProbeFont(fontType, family, log))
            {
                _failedFamily = family;
                return null;
            }

            MethodInfo create = fontType.GetMethod(
                "CreateDynamicFontFromOSFont",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string[]), typeof(int) },
                null);

            if (create == null)
            {
                log.Error("游戏构建里找不到 Font.CreateDynamicFontFromOSFont，无法替换字体。");
                _failedFamily = family;
                return null;
            }

            object font;
            try
            {
                font = create.Invoke(null, new object[] { candidates, 64 });
            }
            catch (Exception e)
            {
                log.Error("加载字体「" + family + "」失败: " + e.Message);
                _failedFamily = family;
                return null;
            }

            if (font == null)
            {
                log.Error("加载字体「" + family + "」失败：系统里没有这个字体。");
                _failedFamily = family;
                return null;
            }

            if (!HasChinese(font))
            {
                log.Error("字体「" + family + "」不含中文或无法渲染中文，已保持游戏默认字体。");
                DestroyObject(font);
                _failedFamily = family;
                return null;
            }

            if (checkCoverage)
            {
                string missingSample;
                int missing = CountMissing(font, out missingSample);
                if (missing > MaxMissingGlyphs)
                {
                    log.Error("字体「" + family + "」缺少 " + missing + " 个游戏用字（" + missingSample
                        + " 等），替换后会出现方块，已保持游戏默认字体。请换用覆盖更全的字体（如思源黑体 / 微软雅黑），"
                        + "或在 config.json 里把 checkCoverage 设为 false 强制应用。");
                    DestroyObject(font);
                    _failedFamily = family;
                    return null;
                }
                if (missing > 0)
                {
                    log.Warn("字体「" + family + "」缺 " + missing + " 个游戏用字（" + missingSample
                        + "），这些字可能显示为方块。");
                }
            }

            _replacement = font;
            _family = family;
            _failedFamily = null;
            return font;
        }

        /// <summary>
        /// 用“只有一个名字”的 Font 探测该名字在游戏里是否真能渲染中文。
        /// 这是本模组最重要的防呆：Unity 的 CreateDynamicFontFromOSFont(string[], int) 在
        /// 所有名字都找不到时依然返回非空对象，后续 HasCharacter / 缺字检查会被
        /// 候选表第二项 Microsoft YaHei 骗过去，于是日志写着“已替换”，屏幕上却是雅黑。
        /// 单名字 Font 没有回退可言，能可靠区分“名字有效”和“被悄悄回退”。
        /// </summary>
        /// <summary>
        /// 把 Unity 真正认识的系统字体名写成 JSON 给 Toys 读。字体页若用 WPF/GDI 的
        /// 另一套名字，游戏会静默回退成雅黑；以这份列表为准才能从根上对上名字。
        /// 字体尚未注册时返回，由控制器的低频轮询重试。
        /// </summary>
        internal void ExportInstalledFontNames(ModLogger log)
        {
            if (_exportedFontNames) return;
            try
            {
                Dictionary<string, BaseFont> registry = FontManager.sFontFactory;
                if (registry == null || registry.Count == 0) return;
                Type fontType = ResolveFontType(registry);
                if (fontType == null) return;
                string[] names = GetInstalledFontNames(fontType);
                if (names == null || names.Length == 0) return;

                string root = CesiumLoader.SDK.IO.Path.LoaderRootDirectory;
                if (string.IsNullOrEmpty(root)) return;
                string dir = System.IO.Path.Combine(root, "data");
                System.IO.Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.Append("[\n");
                for (int i = 0; i < names.Length; i++)
                {
                    if (i > 0) sb.Append(",\n");
                    sb.Append("  ").Append(JsonString(names[i]));
                }
                sb.Append("\n]\n");
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "FontMod-installed-fonts.json"), sb.ToString());
                _exportedFontNames = true;
                log.Info("已导出游戏可识别的系统字体列表（" + names.Length + " 项）");
            }
            catch (Exception e)
            {
                log.Warn("导出系统字体列表失败: " + e.Message);
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

        private static bool ProbeFont(Type fontType, string family, ModLogger log)
        {
            MethodInfo create = fontType.GetMethod(
                "CreateDynamicFontFromOSFont",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(int) },
                null);

            if (create == null)
            {
                // 极老的构建可能只有数组重载；探测跳过，不拦，交给后面的多名字加载。
                log.Warn("游戏构建里没有单名字的 CreateDynamicFontFromOSFont，跳过字体名校验。");
                return true;
            }

            object probe = null;
            try
            {
                try { probe = create.Invoke(null, new object[] { family, 64 }); }
                catch (Exception e)
                {
                    log.Error("字体「" + family + "」加载失败: " + e.Message);
                    return false;
                }

                if (probe == null)
                {
                    log.Error("字体「" + family + "」不是游戏能识别的系统字体名，已保持游戏默认字体。"
                        + "（Toys 列出的名字可能与游戏识别的名字不一致）");
                    return false;
                }

                MethodInfo hasChar = probe.GetType().GetMethod(
                    "HasCharacter", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(char) }, null);
                if (hasChar == null)
                {
                    // 没有这个 API 就无法探测，不拦；后面多名字加载照常。
                    return true;
                }

                int missing = 0;
                var sample = new List<char>(16);
                for (int i = 0; i < CoreChineseSample.Length; i++)
                {
                    char ch = CoreChineseSample[i];
                    bool has;
                    try { has = (bool)hasChar.Invoke(probe, new object[] { ch }); }
                    catch { has = false; }
                    if (has) continue;
                    missing++;
                    if (sample.Count < 16 && !sample.Contains(ch)) sample.Add(ch);
                }

                if (missing > MaxProbeMissing)
                {
                    log.Error("字体「" + family + "」不含足够的常用中文（缺 " + missing + " 个，如 "
                        + new string(sample.ToArray()) + "），已保持游戏默认字体，"
                        + "避免整屏文字被回退字体接管。");
                    return false;
                }

                return true;
            }
            finally
            {
                DestroyObject(probe);
            }
        }

        /// <summary>从已注册字体的 nativeFont 上取到运行时的 UnityEngine.Font 类型。</summary>
        private static Type ResolveFontType(Dictionary<string, BaseFont> registry)
        {
            foreach (KeyValuePair<string, BaseFont> pair in registry)
            {
                DynamicFont dynamicFont = pair.Value as DynamicFont;
                if (dynamicFont == null) continue;
                object native = GetNativeFont(dynamicFont);
                if (native != null) return native.GetType();
            }
            return null;
        }

        private static object GetNativeFont(DynamicFont dynamicFont)
        {
            try { return NativeFontProperty.GetValue(dynamicFont, null); }
            catch { return null; }
        }

        /// <summary>系统字体列表（读不到时返回 null，调用方按“不拦”处理）。</summary>
        private static string[] GetInstalledFontNames(Type fontType)
        {
            try
            {
                MethodInfo method = fontType.GetMethod(
                    "GetOSInstalledFontNames", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method == null) return null;
                return method.Invoke(null, null) as string[];
            }
            catch { return null; }
        }

        private static bool HasChinese(object font)
        {
            try
            {
                MethodInfo method = font.GetType().GetMethod(
                    "HasCharacter", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(char) }, null);
                if (method == null) return true;   // 没有这个 API 就不拦，交给实际渲染
                return (bool)method.Invoke(font, new object[] { '中' });
            }
            catch { return true; }
        }

        /// <summary>
        /// 用游戏文案里的字符集检查缺字。缺字就是方块，所以替换前先数一遍；
        /// 缺太多直接不换，避免整屏方块。返回缺失数量，并把前 24 个缺字写进 <paramref name="sample"/>。
        /// </summary>
        private static int CountMissing(object font, out string sample)
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
            catch { return 0; }
        }

        /// <summary>清掉已经被 <c>FontManager.Clear()</c> 淘汰的字体对象，避免重登后残留旧引用。</summary>
        private void PruneStale(Dictionary<string, BaseFont> registry)
        {
            List<DynamicFont> stale = null;
            foreach (KeyValuePair<DynamicFont, object> pair in _originals)
            {
                bool alive = false;
                foreach (KeyValuePair<string, BaseFont> live in registry)
                {
                    if (ReferenceEquals(live.Value, pair.Key)) { alive = true; break; }
                }
                if (!alive)
                {
                    if (stale == null) stale = new List<DynamicFont>();
                    stale.Add(pair.Key);
                }
            }

            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) _originals.Remove(stale[i]);
        }

        private static bool IsDefaultFont(string name)
        {
            string defaultFont = UIConfig.defaultFont;
            return !string.IsNullOrEmpty(defaultFont)
                && string.Equals(name, defaultFont, StringComparison.Ordinal);
        }

        private static void Warmup(object font)
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
                    if (parameters[0].ParameterType != typeof(string)) continue;
                    if (parameters[1].ParameterType != typeof(int)) continue;
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
                    args[i] = ps[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(ps[i].ParameterType)
                        : null;
                }
                method.Invoke(font, args);
            }
            catch { }
        }

        /// <summary>
        /// FairyGUI 的 <c>Stage</c> 每帧检查 <c>BaseFont.textRebuildFlag</c>，置位后会重排所有文本。
        /// 字段是 protected internal，在不同程序集里只能反射设置；失败也不影响功能（下次字形请求仍会触发重建）。
        /// </summary>
        private static void RequestTextRebuild()
        {
            try
            {
                FieldInfo field = typeof(BaseFont).GetField(
                    "textRebuildFlag", BindingFlags.Static | BindingFlags.NonPublic);
                if (field != null) field.SetValue(null, true);
            }
            catch { }
        }

        /// <summary>
        /// 把已校验的字体推给 SDK 自绘浮层（F8 面板等），让它们和游戏正文用同一套字体。
        /// 老版本 SDK 没有这个 API 时静默跳过，不影响主功能。
        /// </summary>
        private static void PushSdkFont(object font)
        {
            try { WidgetService.SetDefaultFont(font); }
            catch { }
        }

        private void DestroyReplacement()
        {
            if (_replacement == null) return;
            DestroyObject(_replacement);
            _replacement = null;
            _family = null;
        }

        private static void DestroyObject(object font)
        {
            try
            {
                UnityEngine.Object unityObject = font as UnityEngine.Object;
                if (unityObject != null) UnityEngine.Object.Destroy(unityObject);
            }
            catch { }
        }
    }
}

