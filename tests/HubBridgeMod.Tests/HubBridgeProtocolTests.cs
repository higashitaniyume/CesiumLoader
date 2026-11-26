using System.IO;
using HubBridgeMod;
using Xunit;

namespace HubBridgeMod.Tests
{
    public sealed class HubBridgeProtocolTests
    {
        [Theory]
        [InlineData("China", "", "cn")]
        [InlineData("Abroad", "", "int")]
        [InlineData("", "Cn", "cn")]
        [InlineData("", "AOTcn", "cn")]
        [InlineData("", "Int", "int")]
        [InlineData("", "Jp", "int")]
        [InlineData("", "Unknown", "")]
        public void MapRegion_NormalizesCnAndInt(string authTypeName, string rawZone, string expected)
        {
            Assert.Equal(expected, HubBridgeProtocol.MapRegion(authTypeName, rawZone));
        }

        [Fact]
        public void BridgeEndpointInfo_TryParse_ValidJson_Succeeds()
        {
            string json = "{\"port\":51234,\"token\":\"abc123token\",\"pid\":4242,\"updatedAt\":1732600000000}";
            bool ok = BridgeEndpointInfo.TryParse(json, out BridgeEndpointInfo info, out string err);

            Assert.True(ok);
            Assert.Equal("", err);
            Assert.NotNull(info);
            Assert.Equal(51234, info.Port);
            Assert.Equal("abc123token", info.Token);
            Assert.Equal(4242, info.Pid);
            Assert.Equal("http://127.0.0.1:51234", info.BaseUrl());
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{\"port\":0,\"token\":\"abc\"}")]
        [InlineData("{\"port\":70000,\"token\":\"abc\"}")]
        [InlineData("{\"port\":51234,\"token\":\"\"}")]
        public void BridgeEndpointInfo_TryParse_InvalidJson_Fails(string json)
        {
            bool ok = BridgeEndpointInfo.TryParse(json, out BridgeEndpointInfo info, out _);
            Assert.False(ok);
            Assert.Null(info);
        }

        [Fact]
        public void ParseCommandsResponse_ExtractsJoinRoomCommands()
        {
            string json = "{\"commands\":[{\"commandId\":\"c1\",\"action\":\"join_room\",\"gameRoomId\":987654,\"gamePwd\":\"1234\",\"roomServerId\":2},{\"commandId\":\"\",\"action\":\"join_room\"}]}";
            var commands = HubBridgeProtocol.ParseCommandsResponse(json);

            Assert.Single(commands);
            Assert.Equal("c1", commands[0].CommandId);
            Assert.Equal("join_room", commands[0].Action);
            Assert.Equal(987654L, commands[0].GameRoomId);
            Assert.Equal("1234", commands[0].GamePwd);
            Assert.Equal(2, commands[0].RoomServerId);
        }

        [Fact]
        public void EvaluateJoinEligibility_CoversGuardsAndSameRoom()
        {
            Assert.False(HubBridgeProtocol.EvaluateJoinEligibility(false, 1, "Home", false, 0, false, 100, out string code1, out _));
            Assert.Equal("not_logged_in", code1);

            Assert.False(HubBridgeProtocol.EvaluateJoinEligibility(true, 10, "Battle", false, 0, false, 100, out string code2, out _));
            Assert.Equal("wrong_scene", code2);

            Assert.False(HubBridgeProtocol.EvaluateJoinEligibility(true, 10, "Home", false, 0, true, 100, out string code3, out _));
            Assert.Equal("in_match_team", code3);

            Assert.False(HubBridgeProtocol.EvaluateJoinEligibility(true, 10, "Home", true, 99, false, 100, out string code4, out _));
            Assert.Equal("already_in_other_room", code4);

            Assert.True(HubBridgeProtocol.EvaluateJoinEligibility(true, 10, "Home", true, 100, false, 100, out string code5, out _));
            Assert.Equal("already_in_target_room", code5);

            Assert.True(HubBridgeProtocol.EvaluateJoinEligibility(true, 10, "Home", false, 0, false, 100, out string code6, out _));
            Assert.Equal("", code6);
        }

        [Fact]
        public void ResolveEndpointPath_UsesLoaderRootWhenUnconfigured()
        {
            string modsDir = Path.Combine("C:\\Games\\AstralParty\\AstralParty_ModLoader", "mods");
            string resolved = HubBridgeProtocol.ResolveEndpointPath("", modsDir);
            Assert.Equal(Path.Combine("C:\\Games\\AstralParty\\AstralParty_ModLoader", "hub-bridge", "endpoint.json"), resolved);

            Assert.Equal("D:\\custom\\endpoint.json", HubBridgeProtocol.ResolveEndpointPath("D:\\custom\\endpoint.json", modsDir));
        }

        [Fact]
        public void BuildRoomSignature_ReflectsPasswordAndMapChanges()
        {
            var r1 = new BridgeRoomSnapshot { RoomId = 101, Pwd = "aaa", RoomServerId = 1, MapId = 5, MapType = 1, MasterId = 88, IsMaster = true, State = "WAIT", PlayerCount = 1 };
            var r2 = new BridgeRoomSnapshot { RoomId = 101, Pwd = "bbb", RoomServerId = 1, MapId = 5, MapType = 1, MasterId = 88, IsMaster = true, State = "WAIT", PlayerCount = 1 };

            Assert.NotEqual(HubBridgeProtocol.BuildRoomSignature(r1), HubBridgeProtocol.BuildRoomSignature(r2));
        }

        [Fact]
        public void HubBridgeConfig_Read_ClampsOutOfRangeValues()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "HubBridgeModTests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                CesiumLoader.SDK.Configuration.ModConfig.ClearCache();
                var raw = CesiumLoader.SDK.Configuration.ModConfig.ForModId("HubBridgeMod", tempDir);
                raw.Set("Enabled", false);
                raw.Set("PollIntervalMs", 5);
                raw.Set("RequestTimeoutSeconds", 999);
                raw.Set("EndpointPath", "  C:\\test\\endpoint.json  ");

                var cfg = HubBridgeConfig.Read(raw);
                Assert.False(cfg.Enabled);
                Assert.Equal(100, cfg.PollIntervalMs);
                Assert.Equal(30, cfg.RequestTimeoutSeconds);
                Assert.Equal("C:\\test\\endpoint.json", cfg.EndpointPath);
            }
            finally
            {
                CesiumLoader.SDK.Configuration.ModConfig.ClearCache();
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
