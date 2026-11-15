using PvePanelMod;
using Xunit;

namespace PvePanelMod.Tests
{
    /// <summary>
    /// 战斗面板里的名字：先去掉 UBB 标签再截到 7 个字。
    /// 「炫彩ID」这类 mod 改出来的名字带 [color=…] 等标签，服务器原样保存，对局里别的玩家也会收到。
    /// </summary>
    public class ShortNameTests
    {
        [Theory]
        [InlineData("小明", "小明")]
        [InlineData("[color=#FF0000]小明[/color]", "小明")]
        [InlineData("[b][i]小明[/i][/b]", "小明")]
        [InlineData("[size=30][color=#FF0000]小[/color][color=#00FF00]明[/color][/size]", "小明")]
        [InlineData("[s]删除线[/s][sub]下[/sub][sup]上[/sup]", "删除线下上")]
        [InlineData("[COLOR=#fff]大写标签[/COLOR]", "大写标签")]
        public void StripsUbbTags(string raw, string expected)
        {
            Assert.Equal(expected, BattleView.ShortName(raw));
        }

        [Fact]
        public void TruncatesAfterStripping()
        {
            // 去标签之前截的话会得到「[color=…」
            Assert.Equal("一二三四五六七…", BattleView.ShortName("[color=#FF0000]一二三四五六七八九[/color]"));
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("[我的公会]", "[我的公会]")]   // 不是 UBB 标签的方括号保留
        [InlineData("[b][/b]", "[b][/b]")]         // 去完是空的就用原名，总比空白强
        public void EdgeCases(string raw, string expected)
        {
            Assert.Equal(expected, BattleView.ShortName(raw));
        }
    }
}
