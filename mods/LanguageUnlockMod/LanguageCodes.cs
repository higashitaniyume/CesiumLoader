using System;
using Core;

namespace LanguageUnlockMod
{
    /// <summary>
    /// 语言与数据文件 / STR 列 / 字体字段之间的对应关系。
    /// 游戏本身用 <see cref="LanguageType"/> 的 10/22/40/41 四个值；韩语(23)没有任何数据，不在这里。
    /// </summary>
    internal static class LanguageCodes
    {
        internal const string ConfigKey = "language";
        internal const string CodeSimplified = "zh";

        /// <summary>数据文件名（data\{code}.pack / data\{code}.xml）；简中由游戏自带，返回 null。</summary>
        internal static string DataCode(LanguageType language)
        {
            switch (language)
            {
                case LanguageType.English: return "en";
                case LanguageType.Japanese: return "ja";
                case LanguageType.TraditionalChinese: return "tc";
                default: return null;
            }
        }

        /// <summary>STR 表里对应的列名（与 protobuf 生成的属性同名）。</summary>
        internal static string StrColumn(LanguageType language)
        {
            switch (language)
            {
                case LanguageType.English: return "English";
                case LanguageType.Japanese: return "Japanese";
                case LanguageType.TraditionalChinese: return "Traditional";
                default: return null;
            }
        }

        /// <summary>Font 配置表里对应的字段名。</summary>
        internal static string FontKeyProperty(LanguageType language)
        {
            switch (language)
            {
                case LanguageType.English: return "LoadedKeyEN";
                case LanguageType.Japanese: return "LoadedKeyJP";
                case LanguageType.TraditionalChinese: return "LoadedKeyCHT";
                default: return null;
            }
        }

        internal static string ConfigValue(LanguageType language)
        {
            string code = DataCode(language);
            return code ?? CodeSimplified;
        }

        internal static bool TryParseConfig(string value, out LanguageType language)
        {
            language = LanguageType.SimplifiedChinese;
            if (string.IsNullOrEmpty(value)) return true;

            switch (value.Trim().ToLowerInvariant())
            {
                case CodeSimplified:
                case "sc":
                case "zh-cn":
                    language = LanguageType.SimplifiedChinese;
                    return true;
                case "en":
                    language = LanguageType.English;
                    return true;
                case "ja":
                case "jp":
                    language = LanguageType.Japanese;
                    return true;
                case "tc":
                case "zh-tw":
                case "zh-hant":
                    language = LanguageType.TraditionalChinese;
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsSupported(LanguageType language)
        {
            return language == LanguageType.SimplifiedChinese
                || language == LanguageType.English
                || language == LanguageType.Japanese
                || language == LanguageType.TraditionalChinese;
        }

        internal static string DisplayName(LanguageType language)
        {
            switch (language)
            {
                case LanguageType.English: return "English";
                case LanguageType.Japanese: return "日本語";
                case LanguageType.TraditionalChinese: return "繁體中文";
                case LanguageType.SimplifiedChinese: return "简体中文";
                case LanguageType.Korean: return "한국어";
                default: return language.ToString();
            }
        }
    }
}
