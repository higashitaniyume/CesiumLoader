using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Configuration;
using ToysBadgeMod;
using Xunit;

namespace ToysBadgeMod.Tests
{
    public sealed class PresenceProtocolTests
    {
        [Theory]
        [InlineData(null, "cn")]
        [InlineData("", "cn")]
        [InlineData("Cn", "cn")]
        [InlineData("AOTcn", "cn")]
        [InlineData("Int", "int")]
        [InlineData("Jp", "jp")]
        [InlineData("Kr", "kr")]
        [InlineData("Us", "us")]
        [InlineData("EU", "other")]
        public void MapRegion_MapsKnownAndFallbackZones(string rawZone, string expected)
        {
            Assert.Equal(expected, PresenceProtocol.MapRegion(rawZone));
        }

        [Theory]
        [InlineData("abcd", true)]
        [InlineData("room_123-XYZ", true)]
        [InlineData("abc", false)]
        [InlineData("", false)]
        [InlineData("bad code", false)]
        [InlineData("bad/code", false)]
        public void IsValidWatchCode_EnforcesLengthAndCharset(string code, bool expected)
        {
            Assert.Equal(expected, PresenceProtocol.IsValidWatchCode(code));
        }

        [Theory]
        [InlineData("12345678", true)]
        [InlineData("c0387114fc134117a3314740c2e95fda", true)]
        [InlineData("short", false)]
        [InlineData("has space123", false)]
        [InlineData("", false)]
        public void IsValidClientId_EnforcesPrintableAsciiAndBounds(string cid, bool expected)
        {
            Assert.Equal(expected, PresenceProtocol.IsValidClientId(cid));
        }

        [Fact]
        public void SanitizeNick_StripsControlCharsAndTruncatesTo24Chars()
        {
            Assert.Equal("PlayerOne", PresenceProtocol.SanitizeNick(" \tPlayer\u0001One\u007F "));
            string longNick = new string('A', 30);
            Assert.Equal(24, PresenceProtocol.SanitizeNick(longNick).Length);
            Assert.Equal("", PresenceProtocol.SanitizeNick("\r\n\t "));
        }

        [Fact]
        public void SanitizeNick_StripsUbbAndTmpRichTextTagsWhilePreservingPlainBrackets()
        {
            Assert.Equal(
                "这就是上树",
                PresenceProtocol.SanitizeNick("[u][size=28][color=#FFD9B5]这[/color][/size][size=28][color=#FFB2A2]就[/color][/size][size=28][color=#FF8A90]是[/color][/size][color=#FF627E]上[/color][color=#FF3B6B]树[/color][/u]"));
            Assert.Equal(
                "多芒小丸子",
                PresenceProtocol.SanitizeNick("[b][i][color=#9B5CFF,#9B5CFF,#FFD54A,#FFD54A]多芒小丸子[/color][/i][/b]"));
            Assert.Equal(
                "哔哩@知芝酱",
                PresenceProtocol.SanitizeNick("[size=24][i][u][color=#FFF8E7]哔[/color][color=#FFEAE1]哩[/color][color=#FFDCDA]@[/color][color=#FFCFD4]知[/color][color=#FFC1CD]芝[/color][color=#FFB3C7]酱[/color][/u][/i][/size]"));
            Assert.Equal("小红", PresenceProtocol.SanitizeNick("<color=#FF0000><b>小红</b></color>"));
            Assert.Equal("[机巧偶1]", PresenceProtocol.SanitizeNick("[机巧偶1]"));
            Assert.Equal("[战队]小明", PresenceProtocol.SanitizeNick("[战队]小明"));
        }

        [Fact]
        public void DeriveScopedClientId_ProducesDeterministicAccountAndProfileScopedIds()
        {
            string baseCid = "63ead46aed424c4d977589fb7d934ace";
            string cid1 = PresenceProtocol.DeriveScopedClientId(baseCid, "alt1", 10001);
            string cid1Again = PresenceProtocol.DeriveScopedClientId(baseCid, "alt1", 10001);
            string cid2 = PresenceProtocol.DeriveScopedClientId(baseCid, "alt2", 20002);
            string cidProfile = PresenceProtocol.DeriveScopedClientId(baseCid, "sub acc#2", 0);
            string cidLong = PresenceProtocol.DeriveScopedClientId(new string('x', 60), "p1", 9876543210);

            Assert.Equal(cid1, cid1Again);
            Assert.NotEqual(cid1, cid2);
            Assert.True(PresenceProtocol.IsValidClientId(cid1));
            Assert.True(PresenceProtocol.IsValidClientId(cid2));
            Assert.Equal(baseCid + "_pr_subacc2", cidProfile);
            Assert.True(PresenceProtocol.IsValidClientId(cidProfile));
            Assert.True(cidLong.Length <= PresenceProtocol.MaxClientIdLength);
            Assert.True(PresenceProtocol.IsValidClientId(cidLong));
        }

        [Fact]
        public void BuildUrls_TrimTrailingSlashAndEscapeWatchCode()
        {
            Assert.Equal(
                "https://connect.hiynet.com/api/v1/rooms/room_01/join",
                PresenceProtocol.BuildJoinUrl("https://connect.hiynet.com/", "room_01"));
            Assert.Equal(
                "https://connect.hiynet.com/api/v1/rooms/room_01/leave",
                PresenceProtocol.BuildLeaveUrl("https://connect.hiynet.com///", "room_01"));
        }

        [Fact]
        public void BuildBodies_ProduceExpectedJsonFields()
        {
            string joinJson = PresenceProtocol.BuildJoinBody("cn", "测试玩家", "cid12345678", "1.0.0", 12);
            var joinMap = CesiumJson.Deserialize(joinJson) as Dictionary<string, object>;
            Assert.NotNull(joinMap);
            Assert.Equal("cn", joinMap["region"] as string);
            Assert.Equal("测试玩家", joinMap["nick"] as string);
            Assert.Equal("cid12345678", joinMap["clientId"] as string);
            Assert.Equal("1.0.0", joinMap["modVersion"] as string);
            Assert.Equal(12, Convert.ToInt32(joinMap["heroId"]));

            string leaveJson = PresenceProtocol.BuildLeaveBody("jp", "tok_abc");
            var leaveMap = CesiumJson.Deserialize(leaveJson) as Dictionary<string, object>;
            Assert.NotNull(leaveMap);
            Assert.Equal("jp", leaveMap["region"] as string);
            Assert.Equal("tok_abc", leaveMap["token"] as string);
        }

        [Fact]
        public void ParseJoinResponse_ParsesMembersSessionAndConfig()
        {
            string json = "{\"protocol\":1,\"room\":{\"roomId\":\"R7f3k2m9q1\",\"onlineCount\":2,\"capacity\":4,\"createdAt\":1732100000000,\"members\":[{\"memberId\":\"M1\",\"nick\":\"甲\",\"online\":true,\"self\":true,\"joinedAt\":1732100000000},{\"memberId\":\"M2\",\"nick\":\"乙\",\"online\":false,\"self\":false,\"joinedAt\":1732100001000}]},\"session\":{\"token\":\"tok_xyz\",\"expiresAt\":1732103600000},\"cursor\":42,\"epoch\":2,\"config\":{\"pollWaitSeconds\":25},\"serverTime\":1732100000000}";

            JoinResponse res = PresenceProtocol.ParseJoinResponse(json);
            Assert.Equal(1, res.Protocol);
            Assert.Equal("R7f3k2m9q1", res.RoomId);
            Assert.Equal(2, res.OnlineCount);
            Assert.Equal(4, res.Capacity);
            Assert.Equal("tok_xyz", res.Token);
            Assert.Equal(42, res.Cursor);
            Assert.Equal(2, res.Epoch);
            Assert.Equal(25, res.PollWaitSeconds);
            Assert.Equal(2, res.Members.Count);
            Assert.True(res.Members[0].Self);
            Assert.True(res.Members[0].Online);
            Assert.Equal("甲", res.Members[0].NormalizedNick);
            Assert.False(res.Members[1].Online);
            Assert.Equal("乙", res.Members[1].NormalizedNick);
        }

        [Fact]
        public void ParseJoinResponse_ParsesErrorPayload()
        {
            string json = "{\"error\":{\"code\":\"room_full\",\"message\":\"room is full\"}}";
            JoinResponse res = PresenceProtocol.ParseJoinResponse(json);
            Assert.Equal("room_full", res.ErrorCode);
            Assert.Equal("room is full", res.ErrorMessage);
        }

        [Theory]
        [InlineData("APE-1.0.6", BadgeClientKind.ModLab)]
        [InlineData("ape-1.0.5", BadgeClientKind.ModLab)]
        [InlineData("  APE-1.0.0  ", BadgeClientKind.ModLab)]
        [InlineData("1.0.0", BadgeClientKind.Toys)]
        [InlineData("1.0.2", BadgeClientKind.Toys)]
        [InlineData("", BadgeClientKind.Toys)]
        [InlineData(null, BadgeClientKind.Toys)]
        [InlineData("APEX-1.0.0", BadgeClientKind.Toys)]
        public void ResolveClientKind_DetectsModLabPrefixAndDefaultsToToys(string modVersion, BadgeClientKind expected)
        {
            Assert.Equal(expected, PresenceProtocol.ResolveClientKind(modVersion));
        }

        [Fact]
        public void GetTooltipText_ReturnsExpectedTooltipPerClientKind()
        {
            Assert.Equal("该玩家正在使用吉星ModLab", PresenceProtocol.GetTooltipText(BadgeClientKind.ModLab));
            Assert.Equal("该玩家正在使用AstralParty.Toys", PresenceProtocol.GetTooltipText(BadgeClientKind.Toys));
        }

        [Fact]
        public void ParseJoinResponse_ParsesMemberModVersionVariants()
        {
            string json = "{"
                + "\"protocol\":1,"
                + "\"room\":{"
                + "\"roomId\":\"R1\",\"onlineCount\":5,\"capacity\":6,\"createdAt\":1732100000000,"
                + "\"members\":["
                + "{\"memberId\":\"M1\",\"nick\":\"P1\",\"modVersion\":\"APE-1.0.6\",\"online\":true,\"self\":false,\"joinedAt\":1},"
                + "{\"memberId\":\"M2\",\"nick\":\"P2\",\"modversion\":\"ape-1.0.5\",\"online\":true,\"self\":false,\"joinedAt\":2},"
                + "{\"memberId\":\"M3\",\"nick\":\"P3\",\"mod_version\":\"APE-2.0\",\"online\":true,\"self\":false,\"joinedAt\":3},"
                + "{\"memberId\":\"M4\",\"nick\":\"P4\",\"modVersion\":\"1.0.2\",\"online\":true,\"self\":true,\"joinedAt\":4},"
                + "{\"memberId\":\"M5\",\"nick\":\"P5\",\"online\":true,\"self\":false,\"joinedAt\":5}"
                + "]}}";

            JoinResponse res = PresenceProtocol.ParseJoinResponse(json);
            Assert.Equal(5, res.Members.Count);
            Assert.Equal("APE-1.0.6", res.Members[0].ModVersion);
            Assert.Equal(BadgeClientKind.ModLab, PresenceProtocol.ResolveClientKind(res.Members[0].ModVersion));
            Assert.Equal("ape-1.0.5", res.Members[1].ModVersion);
            Assert.Equal(BadgeClientKind.ModLab, PresenceProtocol.ResolveClientKind(res.Members[1].ModVersion));
            Assert.Equal("APE-2.0", res.Members[2].ModVersion);
            Assert.Equal(BadgeClientKind.ModLab, PresenceProtocol.ResolveClientKind(res.Members[2].ModVersion));
            Assert.Equal("1.0.2", res.Members[3].ModVersion);
            Assert.Equal(BadgeClientKind.Toys, PresenceProtocol.ResolveClientKind(res.Members[3].ModVersion));
            Assert.Equal("", res.Members[4].ModVersion);
            Assert.Equal(BadgeClientKind.Toys, PresenceProtocol.ResolveClientKind(res.Members[4].ModVersion));
        }
    }
}
