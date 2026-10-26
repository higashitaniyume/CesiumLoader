using Xunit;

namespace CombatOddsMod.Tests
{
    /// <summary>
    /// 剩余手牌的口径: 队友用游戏自己的 CardCount, 自己优先用本机手牌列表;
    /// 图标地址从游戏文字模板里取, 取不到时返回 null(调用方退回公共卡牌图标)。
    /// </summary>
    public class HandCountRulesTests
    {
        [Fact]
        public void Self_UsesLiveHandList()
        {
            // 5056 之类只更新列表不回写计数的同步, 自己的牌以列表为准。
            Assert.Equal(4, HandCountRules.Resolve(serverCardCount: 5, selfHandCount: 4, isSelf: true));
            Assert.Equal(0, HandCountRules.Resolve(serverCardCount: 5, selfHandCount: 0, isSelf: true));
        }

        [Fact]
        public void Self_FallsBackToCountWhenListUnavailable()
        {
            Assert.Equal(5, HandCountRules.Resolve(serverCardCount: 5, selfHandCount: -1, isSelf: true));
        }

        [Fact]
        public void Others_AlwaysUseGameCardCount()
        {
            // 队友的牌被服务器掩码, 列表不可信; 数量看 CardCount(与游戏玩家信息面板同源)。
            Assert.Equal(5, HandCountRules.Resolve(serverCardCount: 5, selfHandCount: 4, isSelf: false));
            Assert.Equal(0, HandCountRules.Resolve(serverCardCount: 0, selfHandCount: 9, isSelf: false));
        }

        [Fact]
        public void UnknownCount_NeverShowsNegative()
        {
            Assert.Equal(0, HandCountRules.Resolve(serverCardCount: -3, selfHandCount: -1, isSelf: false));
            Assert.Equal(0, HandCountRules.Resolve(serverCardCount: -1, selfHandCount: -1, isSelf: true));
        }

        [Fact]
        public void IconUrl_IsExtractedFromUbbAndHtmlTemplates()
        {
            Assert.Equal("ui://vflhnh8dorg71",
                HandCountRules.ExtractIconUrl("[img]ui://vflhnh8dorg71[/img]0/1"));
            Assert.Equal("ui://abc123",
                HandCountRules.ExtractIconUrl("<img src='ui://abc123'/>5"));
            Assert.Equal("ui://xyz789",
                HandCountRules.ExtractIconUrl("手牌 [img]ui://xyz789[/img]"));
        }

        [Fact]
        public void IconUrl_IsNullWhenTextHasNoIcon()
        {
            Assert.Null(HandCountRules.ExtractIconUrl(null));
            Assert.Null(HandCountRules.ExtractIconUrl(string.Empty));
            Assert.Null(HandCountRules.ExtractIconUrl("0/1"));
            Assert.Null(HandCountRules.ExtractIconUrl("ui://"));
        }

        [Fact]
        public void Format_NeverPrintsNegativeCounts()
        {
            Assert.Equal("0", HandCountRules.Format(0));
            Assert.Equal("12", HandCountRules.Format(12));
            Assert.Equal("?", HandCountRules.Format(-1));
        }
    }
}
