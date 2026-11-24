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
    }
}
