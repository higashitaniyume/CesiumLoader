using System;
using AstralParty.Agent;
using AstralParty.AgentMod.Bridge;
using Xunit;

namespace AstralParty.AgentMod.Tests
{
    /// <summary>
    /// 命令/结果的 JSON 解析。MCP server 用 System.Text.Json 写, 游戏内 mod 用 CesiumJson 读,
    /// 两边的字段名/类型约定就靠这一组用例守住。
    /// </summary>
    public class CommandParserTests
    {
        private static BridgeCommand ParseOk(string json)
        {
            BridgeCommand cmd;
            string error;
            Assert.True(BridgeCommandParser.TryParse(json, out cmd, out error), "本应解析成功, error=" + error);
            return cmd;
        }

        [Fact]
        public void 正常命令_字段齐全()
        {
            var cmd = ParseOk("{\"schema\":1,\"id\":\"c1\",\"seq\":7,\"tool\":\"throw_dice\",\"issuedAtMs\":1727000000000,\"args\":{\"noOper\":true}}");

            Assert.Equal("c1", cmd.Id);
            Assert.Equal("throw_dice", cmd.Tool);
            Assert.Equal(7, cmd.Seq);
            Assert.Equal(1727000000000, cmd.IssuedAtMs);
            Assert.True(cmd.GetBool("noOper", false));
        }

        [Fact]
        public void 命令名是MCP外部名_去掉astral前缀后的形式()
        {
            // 协议里的 tool 字段用短名(throw_dice), 外部工具名是 astral_throw_dice
            var cmd = ParseOk("{\"id\":\"x\",\"tool\":\"" + AgentBridgeLayout.Tool.ShopBuy + "\",\"args\":{}}");
            Assert.Equal("shop_buy", cmd.Tool);
            Assert.Equal("astral_" + cmd.Tool, AgentBridgeLayout.ExternalToolName(AgentBridgeLayout.Tool.ShopBuy));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json at all")]
        [InlineData("[1,2,3]")]
        public void 坏JSON_解析失败并给原因(string json)
        {
            BridgeCommand cmd;
            string error;
            Assert.False(BridgeCommandParser.TryParse(json, out cmd, out error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void 缺tool或id_解析失败()
        {
            BridgeCommand cmd;
            string error;
            Assert.False(BridgeCommandParser.TryParse("{\"id\":\"a\"}", out cmd, out error));
            Assert.Contains("tool", error);

            Assert.False(BridgeCommandParser.TryParse("{\"tool\":\"move\"}", out cmd, out error));
            Assert.Contains("id", error);
        }

        [Fact]
        public void 各种类型取值()
        {
            var cmd = ParseOk("{\"id\":\"a\",\"tool\":\"t\",\"args\":{\"i\":3,\"l\":9007199254740993,\"d\":2.5,\"b\":true,\"s\":\"hi\",\"list\":[1,2,3]}}");

            Assert.Equal(3, cmd.GetInt("i", 0));
            Assert.Equal(2.5, cmd.GetDouble("d", 0));
            Assert.True(cmd.GetBool("b", false));
            Assert.Equal("hi", cmd.GetString("s", null));
            Assert.Equal(new[] { 1, 2, 3 }, cmd.GetIntList("list"));
            Assert.True(cmd.Has("i"));
            Assert.False(cmd.Has("nope"));
        }

        [Fact]
        public void 缺字段时用默认值_不抛异常()
        {
            var cmd = ParseOk("{\"id\":\"a\",\"tool\":\"t\"}");

            Assert.Equal(9, cmd.GetInt("missing", 9));
            Assert.Equal(9L, cmd.GetLong("missing", 9));
            Assert.False(cmd.GetBool("missing", false));
            Assert.True(cmd.GetBool("missing", true));
            Assert.Equal("d", cmd.GetString("missing", "d"));
            Assert.Empty(cmd.GetIntList("missing"));
            Assert.False(cmd.TryGetIntFlexible("cardId", "cardIds", out _));
        }

        [Fact]
        public void TryGetIntFlexible_单值或列表都能取()
        {
            int v;
            Assert.True(ParseOk("{\"id\":\"a\",\"tool\":\"t\",\"args\":{\"cardId\":123}}").TryGetIntFlexible("cardId", "cardIds", out v));
            Assert.Equal(123, v);

            Assert.True(ParseOk("{\"id\":\"a\",\"tool\":\"t\",\"args\":{\"cardIds\":[456,789]}}").TryGetIntFlexible("cardId", "cardIds", out v));
            Assert.Equal(456, v);

            // 0 视为"没给"(SDK 里 0 是无效卡牌)
            Assert.False(ParseOk("{\"id\":\"a\",\"tool\":\"t\",\"args\":{\"cardId\":0}}").TryGetIntFlexible("cardId", "cardIds", out v));
        }

        [Fact]
        public void 结果成功_JSON带ok与detail()
        {
            var res = BridgeResult.Success("c1", "move", "已发送 移动", 12);
            string json = res.ToJson();

            Assert.Contains("\"Ok\":true", json);
            Assert.Contains("已发送 移动", json);
            Assert.Contains("\"LatencyMs\":12", json);
            Assert.Contains("\"Id\":\"c1\"", json);
        }

        [Fact]
        public void 结果失败_JSON带ok为false与错误码()
        {
            var res = BridgeResult.Failure("c2", "shop_buy", AgentBridgeLayout.Code.BadArgs, "槽位越界");
            string json = res.ToJson();

            Assert.Contains("\"Ok\":false", json);
            Assert.Contains("bad_args", json);
            Assert.Contains("槽位越界", json);
        }

        [Fact]
        public void 结果JSON能被读回来_两端约定一致()
        {
            var res = BridgeResult.Success("c9", "select_relic", "已发送", 3);
            string json = res.ToJson();

            object node;
            Assert.True(CesiumLoader.SDK.Configuration.CesiumJson.TryDeserialize(json, out node));
            var map = (System.Collections.Generic.IDictionary<string, object>)node;

            Assert.True(CesiumLoader.SDK.Configuration.CesiumJson.GetBool(map, "Ok", false));
            Assert.Equal("c9", CesiumLoader.SDK.Configuration.CesiumJson.GetString(map, "Id", null));
            Assert.Equal("ok", CesiumLoader.SDK.Configuration.CesiumJson.GetString(map, "Code", null));
        }
    }
}
