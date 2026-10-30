using System;
using Core;

namespace LanguageUnlockMod
{
    /// <summary>
    /// 语言切换在游戏侧的落点：
    ///  - <c>Settings.Languages</c>   设置界面下拉框的数据来源（国服配置里本来就有 4 条）
    ///  - <c>Settings.LanguageDict</c> 只被 <c>UIManager.LoadFairyGUIContent</c> 用来找界面文案包
    ///  - <c>TranslationHelper.strings</c> FairyGUI 的界面文案表，由 <c>UIPackage.SetStringsSource</c> 写入
    ///
    /// 国服没有英 / 日 / 繁的界面文案包，直接切语言会让 <c>LoadFairyGUIContent</c> 抛异常卡在登录。
    /// 这里把目标语言的 LanguageDict 条目摘掉：加载函数找不到条目会打一条日志直接 return，
    /// 于是我们自己写入的 TranslationHelper.strings 不会被覆盖，登录流程也不会中断。
    /// </summary>
    internal static class LanguageRuntime
    {
        private sealed class EntrySpec
        {
            internal LanguageType Language;
            internal int DataIndex;
            internal int DescriptionId;
            internal string FileContentKey;
            internal string Simplified;
            internal string English;
            internal string Japanese;
            internal string Traditional;
        }

        private static readonly EntrySpec[] Specs =
        {
            new EntrySpec
            {
                Language = LanguageType.English, DataIndex = 0, DescriptionId = 1410, FileContentKey = "English",
                Simplified = "英文", English = "English", Japanese = "英語", Traditional = "英文"
            },
            new EntrySpec
            {
                Language = LanguageType.Japanese, DataIndex = 1, DescriptionId = 1422, FileContentKey = "Japanese",
                Simplified = "日文", English = "Japanese", Japanese = "日本語", Traditional = "日文"
            },
            new EntrySpec
            {
                Language = LanguageType.SimplifiedChinese, DataIndex = 2, DescriptionId = 1440,
                FileContentKey = "Simplified Chinese",
                Simplified = "简体中文", English = "Simplified Chinese", Japanese = "簡体字中国語", Traditional = "簡體中文"
            },
            new EntrySpec
            {
                Language = LanguageType.TraditionalChinese, DataIndex = 3, DescriptionId = 1441,
                FileContentKey = "Traditional Chinese",
                Simplified = "繁体中文", English = "Traditional Chinese", Japanese = "繁体字中国語", Traditional = "繁體中文"
            }
        };

        /// <summary>补齐国服配置里可能缺失的语言条目与文案（该版本 4 条都在，这里只做兜底）。</summary>
        internal static void EnsureLanguageEntries(Action<string> warn)
        {
            SettingsConfigure settings = StaticConfigure.Settings;
            if (settings == null) return;

            for (int i = 0; i < Specs.Length; i++)
            {
                EntrySpec spec = Specs[i];
                if (!Contains(settings, spec.Language))
                {
                    var entry = new SettingsLanguageConfigure();
                    bool ok = Reflective.Set(entry, "LanguageType", spec.Language)
                              && Reflective.Set(entry, "DataIndex", spec.DataIndex)
                              && Reflective.Set(entry, "DescriptionID", spec.DescriptionId)
                              && Reflective.Set(entry, "FileContentKey", spec.FileContentKey);
                    if (ok)
                    {
                        settings.Languages.Add(entry);
                        if (warn != null) warn("补回语言条目: " + LanguageCodes.DisplayName(spec.Language));
                    }
                    else if (warn != null)
                    {
                        warn("无法创建语言条目: " + spec.Language);
                    }
                }

                EnsureLabel(spec, warn);
            }
        }

        private static bool Contains(SettingsConfigure settings, LanguageType language)
        {
            var languages = settings.Languages;
            for (int i = 0; i < languages.Count; i++)
            {
                if (languages[i] != null && languages[i].LanguageType == language) return true;
            }
            return false;
        }

        private static void EnsureLabel(EntrySpec spec, Action<string> warn)
        {
            STRSettingsConfigure strings = StaticConfigure.STRSettings;
            if (strings == null) return;
            if (strings.LocalDict.ContainsKey(spec.DescriptionId)) return;

            var row = new STRSettingsLocalConfigure();
            bool ok = Reflective.Set(row, "Id", spec.DescriptionId)
                      && Reflective.Set(row, "Simplified", spec.Simplified)
                      && Reflective.Set(row, "English", spec.English)
                      && Reflective.Set(row, "Japanese", spec.Japanese)
                      && Reflective.Set(row, "Traditional", spec.Traditional);
            if (ok)
            {
                strings.LocalDict[spec.DescriptionId] = row;
            }
            else if (warn != null)
            {
                warn("无法写入语言文案: " + spec.DescriptionId);
            }
        }

        /// <summary>
        /// 摘掉目标语言的界面文案条目。返回是否原本存在（简中条目保持不动）。
        /// </summary>
        internal static bool HideLanguageDictionaryEntry(LanguageType language)
        {
            SettingsConfigure settings = StaticConfigure.Settings;
            if (settings == null) return false;
            if (language == LanguageType.SimplifiedChinese) return false;
            return settings.LanguageDict.Remove((int)language);
        }

        /// <summary>把目标语言的 FairyGUI 界面文案写进 TranslationHelper。</summary>
        internal static bool ApplyStrings(LanguageData data, LanguageType language, out string error)
        {
            error = null;
            if (language == LanguageType.SimplifiedChinese) return true;  // 游戏自己会加载简中文案

            string xml = data.GetXml(language, out error);
            if (string.IsNullOrEmpty(xml)) return false;

            try
            {
                FairyGUI.UIPackage.SetStringsSource(new FairyGUI.Utils.XML(xml));
                return true;
            }
            catch (Exception e)
            {
                error = "解析界面文案失败: " + e.Message;
                return false;
            }
        }

        /// <summary>把首页横幅的外语图片槽位指回国服简中图，避免加载国服没有的资源。</summary>
        internal static int AlignBannerImages()
        {
            BannerConfigure banner = StaticConfigure.Banner;
            if (banner == null) return 0;

            int changed = 0;
            var infos = banner.Infos;
            for (int i = 0; i < infos.Count; i++)
            {
                BannerInfoConfigure info = infos[i];
                if (info == null) continue;
                changed += BannerImageAlign.Align(info.BannerImages);
                changed += BannerImageAlign.Align(info.BannerImagesSFW);
            }
            return changed;
        }

        /// <summary>设置界面下拉框里某一项显示的文本；配置缺失时退回语言名。</summary>
        internal static string Label(SettingsLanguageConfigure entry)
        {
            if (entry == null) return string.Empty;
            string text = string.Empty;
            try { text = UI.UIHelper.GetLocal(entry.DescriptionID, UI.UIStringType.Settings); }
            catch { }
            if (string.IsNullOrEmpty(text)) return LanguageCodes.DisplayName(entry.LanguageType);
            return text;
        }
    }
}
