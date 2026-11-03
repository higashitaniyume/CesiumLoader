using System;
using System.Collections.Generic;
using Xunit;

namespace ShopRelicPeekMod.Tests
{
    public class ShopRelicTrackerTests
    {
        private readonly ShopRelicTracker _tracker;
        private readonly ShopRelicPeekConfig _config;

        public ShopRelicTrackerTests()
        {
            _tracker = new ShopRelicTracker
            {
                CardNameResolver = id => id switch
                {
                    101 => "冲刺卡",
                    102 => "急救包",
                    103 => "换位卡",
                    _ => $"卡牌{id}"
                },
                RelicInfoResolver = id => id switch
                {
                    501 => new RelicItemInfo("小猪存钱罐", 1),
                    502 => new RelicItemInfo("标靶", 1),
                    503 => new RelicItemInfo("缓冲盾牌", 2),
                    504 => new RelicItemInfo("重锤", 3),
                    505 => new RelicItemInfo("护目镜", 2),
                    506 => new RelicItemInfo("防弹背心", 1),
                    _ => new RelicItemInfo($"筹码{id}", 0)
                },
                NickResolver = pid => pid switch
                {
                    1001 => "PlayerRed",
                    1002 => "PlayerGreen",
                    1003 => "PlayerBlue",
                    1004 => "PlayerYellow",
                    _ => $"User{pid}"
                },
                SlotResolver = pid => pid switch
                {
                    1001 => 0, // 一楼 Red
                    1002 => 1, // 二楼 Green
                    1003 => 2, // 三楼 Blue
                    1004 => 3, // 四楼 Yellow
                    _ => -1
                }
            };

            _config = new ShopRelicPeekConfig
            {
                Enabled = true,
                ShowShopCards = true,
                ShowRelicCandidates = true,
                ColorRelicByQuality = true,
                ShopDisplayDurationSeconds = 20,
                RelicDisplayDurationSeconds = 20,
                ShowFloorTag = true
            };
        }

        [Fact]
        public void RelicCandidates_InitialOffer_DisplaysCandidatesWithoutHistory()
        {
            var now = DateTime.UtcNow;
            _tracker.RecordRelicCandidates(1001, 10, new[] { 501, 502, 503 });

            Assert.True(_tracker.HasActiveRelic(now, 20));
            var text = _tracker.BuildRelicOverlayText(_config, now);

            Assert.NotNull(text);
            Assert.Contains("[color=#FF4646][b]【一楼】PlayerRed[/b][/color]", text);
            Assert.Contains("候选筹码:", text);
            Assert.Contains("小猪存钱罐", text);
            Assert.Contains("标靶", text);
            Assert.Contains("缓冲盾牌", text);
            Assert.DoesNotContain("历史", text);
        }

        [Fact]
        public void RelicCandidates_Reroll_PreservesHistoryAndUpdatesCurrent()
        {
            var now = DateTime.UtcNow;
            _tracker.RecordRelicCandidates(1002, 10, new[] { 501, 502, 503 });
            // Reroll with new Sn
            _tracker.RecordRelicCandidates(1002, 20, new[] { 504, 505, 506 });

            var text = _tracker.BuildRelicOverlayText(_config, now);
            Assert.NotNull(text);
            Assert.Contains("[color=#94FF46][b]【二楼】PlayerGreen[/b][/color]", text);
            Assert.Contains("筹码候选历史:", text);
            Assert.Contains("第 1 次:", text);
            Assert.Contains("小猪存钱罐", text);
            Assert.Contains("当前候选:", text);
            Assert.Contains("重锤", text);
        }

        [Fact]
        public void RelicCandidates_MultipleRerolls_AppendsAllHistory()
        {
            var now = DateTime.UtcNow;
            _tracker.RecordRelicCandidates(1003, 10, new[] { 501 });
            _tracker.RecordRelicCandidates(1003, 20, new[] { 502 });
            _tracker.RecordRelicCandidates(1003, 30, new[] { 503 });

            var text = _tracker.BuildRelicOverlayText(_config, now);
            Assert.NotNull(text);
            Assert.Contains("第 1 次:", text);
            Assert.Contains("第 2 次:", text);
            Assert.Contains("当前候选:", text);
            Assert.Contains("缓冲盾牌", text);
        }

        [Fact]
        public void RelicSelected_ShowsResultDuringDuration_ThenExpires()
        {
            var selectTime = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            _tracker.RecordRelicCandidates(1002, 10, new[] { 501, 502, 503 });
            _tracker.RecordRelicCandidates(1002, 20, new[] { 504, 505, 506 });

            // Player selects relic 504 (重锤)
            _tracker.RecordRelicSelected(1002, 504, selectTime);

            // Within 20s: stays visible, shows candidates, highlight on chosen relic, and final selection line
            var check1 = selectTime.AddSeconds(5);
            Assert.True(_tracker.HasActiveRelic(check1, 20));
            var text = _tracker.BuildRelicOverlayText(_config, check1);
            Assert.NotNull(text);
            Assert.Contains("最终选定:", text);
            Assert.Contains("重锤", text);
            Assert.Contains("已选", text);

            // After 20s: expires
            var check2 = selectTime.AddSeconds(21);
            Assert.False(_tracker.HasActiveRelic(check2, 20));
            Assert.Null(_tracker.BuildRelicOverlayText(_config, check2));
        }

        [Fact]
        public void ShopEnter_DisplaysCardsEncountered_Independently()
        {
            var now = DateTime.UtcNow;
            _tracker.RecordShopEnter(1003, "PVE商店", new[] { 101, 102, 103 }, null);

            Assert.True(_tracker.HasActiveShop(now, 20));
            Assert.False(_tracker.HasActiveRelic(now, 20)); // Relic overlay is completely separate and inactive!

            var text = _tracker.BuildShopOverlayText(_config, now);
            Assert.NotNull(text);
            Assert.Contains("[color=#4386F4][b]【三楼】PlayerBlue[/b][/color]", text);
            Assert.Contains("PVE商店遇牌:", text);
            Assert.Contains("冲刺卡", text);
            Assert.Contains("急救包", text);
            Assert.Contains("换位卡", text);
        }

        [Fact]
        public void ShopEnter_SoldCards_RenderWithSoldTag()
        {
            var now = DateTime.UtcNow;
            _tracker.RecordShopEnter(1001, "PVP商店", new[] { 101, 102 }, new[] { false, true });

            var text = _tracker.BuildShopOverlayText(_config, now);
            Assert.NotNull(text);
            Assert.Contains("[color=#E6F0FF]冲刺卡[/color]", text);
            Assert.Contains("[color=#888888]急救包(已售)[/color]", text);
        }

        [Fact]
        public void ShopClose_ExpiresAfterConfiguredDuration()
        {
            var enterTime = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            _tracker.RecordShopEnter(1004, "商店", new[] { 101 }, null);

            var closeTime = enterTime.AddSeconds(5);
            _tracker.RecordShopClose(1004, closeTime);

            // Within 20s duration: still visible
            var check1 = closeTime.AddSeconds(10);
            Assert.True(_tracker.HasActiveShop(check1, 20));
            Assert.NotNull(_tracker.BuildShopOverlayText(_config, check1));

            // Past 20s duration: expired
            var check2 = closeTime.AddSeconds(21);
            Assert.False(_tracker.HasActiveShop(check2, 20));
            Assert.Null(_tracker.BuildShopOverlayText(_config, check2));
        }

        [Fact]
        public void Players_SortedBySeatOrder_OneToFour_WithCorrectColors()
        {
            var now = DateTime.UtcNow;
            // Add in reverse order: 4, 3, 2, 1
            _tracker.RecordRelicCandidates(1004, 1, new[] { 501 });
            _tracker.RecordRelicCandidates(1003, 2, new[] { 502 });
            _tracker.RecordRelicCandidates(1002, 3, new[] { 503 });
            _tracker.RecordRelicCandidates(1001, 4, new[] { 504 });

            var text = _tracker.BuildRelicOverlayText(_config, now);
            Assert.NotNull(text);

            int idx1 = text.IndexOf("[color=#FF4646][b]【一楼】PlayerRed[/b][/color]", StringComparison.Ordinal);
            int idx2 = text.IndexOf("[color=#94FF46][b]【二楼】PlayerGreen[/b][/color]", StringComparison.Ordinal);
            int idx3 = text.IndexOf("[color=#4386F4][b]【三楼】PlayerBlue[/b][/color]", StringComparison.Ordinal);
            int idx4 = text.IndexOf("[color=#FFB346][b]【四楼】PlayerYellow[/b][/color]", StringComparison.Ordinal);

            Assert.True(idx1 >= 0);
            Assert.True(idx2 > idx1);
            Assert.True(idx3 > idx2);
            Assert.True(idx4 > idx3);
        }

        [Fact]
        public void Clear_WipesBothShopAndRelicState()
        {
            var now = DateTime.UtcNow;
            _tracker.RecordShopEnter(1001, "商店", new[] { 101 }, null);
            _tracker.RecordRelicCandidates(1002, 1, new[] { 501 });

            Assert.True(_tracker.HasActiveShop(now, 20));
            Assert.True(_tracker.HasActiveRelic(now, 20));

            // Instant clear triggered
            _tracker.Clear();

            Assert.False(_tracker.HasActiveShop(now, 20));
            Assert.False(_tracker.HasActiveRelic(now, 20));
            Assert.Null(_tracker.BuildShopOverlayText(_config, now));
            Assert.Null(_tracker.BuildRelicOverlayText(_config, now));
        }

        [Fact]
        public void QualityColors_MatchExpectedValues()
        {
            Assert.Equal("#5AA9FF", ShopRelicTracker.QualityColor(1)); // 普通
            Assert.Equal("#D26DFF", ShopRelicTracker.QualityColor(2)); // 稀有
            Assert.Equal("#FFAA00", ShopRelicTracker.QualityColor(3)); // 传说
            Assert.Equal("#E0E0E0", ShopRelicTracker.QualityColor(0)); // 未知
        }
    }
}
