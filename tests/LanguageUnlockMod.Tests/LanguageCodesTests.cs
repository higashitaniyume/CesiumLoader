using Xunit;

namespace LanguageUnlockMod.Tests
{
    public class LanguageCodesTests
    {
        [Theory]
        [InlineData("zh")]
        [InlineData("")]
        [InlineData(null)]
        public void TryParseConfig_TreatsEmptyAsSimplified(string value)
        {
            Assert.True(LanguageCodes.TryParseConfig(value, out LanguageType language));
            Assert.Equal(LanguageType.SimplifiedChinese, language);
        }

        [Theory]
        [InlineData("en", LanguageType.English)]
        [InlineData("EN", LanguageType.English)]
        [InlineData("ja", LanguageType.Japanese)]
        [InlineData("jp", LanguageType.Japanese)]
        [InlineData("tc", LanguageType.TraditionalChinese)]
        [InlineData("zh-tw", LanguageType.TraditionalChinese)]
        public void TryParseConfig_AcceptsCodes(string value, LanguageType expected)
        {
            Assert.True(LanguageCodes.TryParseConfig(value, out LanguageType language));
            Assert.Equal(expected, language);
        }

        [Fact]
        public void TryParseConfig_RejectsUnknown()
        {
            Assert.False(LanguageCodes.TryParseConfig("ko", out _));
            Assert.False(LanguageCodes.TryParseConfig("fr", out _));
        }

        [Fact]
        public void DataCode_OnlyCoversShippedLanguages()
        {
            Assert.Null(LanguageCodes.DataCode(LanguageType.SimplifiedChinese));
            Assert.Equal("en", LanguageCodes.DataCode(LanguageType.English));
            Assert.Equal("ja", LanguageCodes.DataCode(LanguageType.Japanese));
            Assert.Equal("tc", LanguageCodes.DataCode(LanguageType.TraditionalChinese));
            Assert.Null(LanguageCodes.DataCode(LanguageType.Korean));
        }

        [Fact]
        public void Columns_MatchProtobufProperties()
        {
            Assert.Equal("English", LanguageCodes.StrColumn(LanguageType.English));
            Assert.Equal("Japanese", LanguageCodes.StrColumn(LanguageType.Japanese));
            Assert.Equal("Traditional", LanguageCodes.StrColumn(LanguageType.TraditionalChinese));
            Assert.Null(LanguageCodes.StrColumn(LanguageType.SimplifiedChinese));

            Assert.Equal("LoadedKeyEN", LanguageCodes.FontKeyProperty(LanguageType.English));
            Assert.Equal("LoadedKeyJP", LanguageCodes.FontKeyProperty(LanguageType.Japanese));
            Assert.Equal("LoadedKeyCHT", LanguageCodes.FontKeyProperty(LanguageType.TraditionalChinese));
        }

        [Fact]
        public void ConfigValue_RoundTrips()
        {
            foreach (LanguageType language in new[]
                     {
                         LanguageType.SimplifiedChinese, LanguageType.English,
                         LanguageType.Japanese, LanguageType.TraditionalChinese
                     })
            {
                string code = LanguageCodes.ConfigValue(language);
                Assert.True(LanguageCodes.TryParseConfig(code, out LanguageType parsed));
                Assert.Equal(language, parsed);
            }
        }
    }
}
