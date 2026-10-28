using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.Configuration
{
    /// <summary>
    /// 单个配置项的多语言说明。由 <see cref="ModConfig.Describe"/> 写进
    /// <c>config.json</c> 的保留键 <c>_comments</c>，Toys 会按当前界面语言显示。
    ///
    /// 用法:
    /// <code>
    /// config.SetDefault("cooldownSeconds", 2, ConfigDoc.Of(
    ///     "冷却秒数", "冷卻秒數", "Cooldown in seconds", "クールダウン（秒）"));
    /// </code>
    ///
    /// 说明是纯文本，不解析 HTML。某一种语言留空/null 时不写入该语言。
    /// </summary>
    public sealed class ConfigDoc
    {
        public const string SimplifiedChinese = "zh-Hans";
        public const string TraditionalChinese = "zh-Hant";
        public const string English = "en";
        public const string Japanese = "ja";

        /// <summary>简体中文说明。</summary>
        public string ZhHans { get; set; }

        /// <summary>繁体中文说明。</summary>
        public string ZhHant { get; set; }

        /// <summary>英文说明。</summary>
        public string En { get; set; }

        /// <summary>日文说明。</summary>
        public string Ja { get; set; }

        /// <summary>四种语言共用同一句说明。</summary>
        public static ConfigDoc All(string text)
        {
            return Of(text, text, text, text);
        }

        /// <summary>按语言分别指定说明；未提供的语言不写入。</summary>
        public static ConfigDoc Of(string zhHans, string zhHant = null, string en = null, string ja = null)
        {
            return new ConfigDoc
            {
                ZhHans = zhHans,
                ZhHant = zhHant,
                En = en,
                Ja = ja
            };
        }

        /// <summary>是否没有任何可写入的说明。</summary>
        public bool IsEmpty
        {
            get
            {
                return string.IsNullOrWhiteSpace(ZhHans)
                    && string.IsNullOrWhiteSpace(ZhHant)
                    && string.IsNullOrWhiteSpace(En)
                    && string.IsNullOrWhiteSpace(Ja);
            }
        }

        internal IEnumerable<KeyValuePair<string, string>> ToPairs()
        {
            if (!string.IsNullOrWhiteSpace(ZhHans))
                yield return new KeyValuePair<string, string>(SimplifiedChinese, ZhHans);
            if (!string.IsNullOrWhiteSpace(ZhHant))
                yield return new KeyValuePair<string, string>(TraditionalChinese, ZhHant);
            if (!string.IsNullOrWhiteSpace(En))
                yield return new KeyValuePair<string, string>(English, En);
            if (!string.IsNullOrWhiteSpace(Ja))
                yield return new KeyValuePair<string, string>(Japanese, Ja);
        }
    }
}
