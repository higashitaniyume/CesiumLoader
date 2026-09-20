using System.Collections.Generic;
using CesiumLoader.SDK;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>CesiumJson 的序列化 / 反序列化 / 忽略属性。</summary>
    public class JsonTests
    {
        private sealed class Sample
        {
            public int Count = 3;
            public float Ratio = 1.5f;
            public bool Enabled = true;
            public string Label = "he said \"hi\"\n\t\\end";
            public List<int> Numbers = new List<int> { 1, 2, 3 };
            public Dictionary<string, object> Extra = new Dictionary<string, object> { { "k", "v" } };

            /// <summary>派生视图: 不应被序列化。</summary>
            [CesiumJsonIgnore]
            public int Doubled { get { return Count * 2; } }
        }

        private sealed class Nested
        {
            public string Name = "outer";
            public Sample Inner = new Sample();
        }

        [Fact]
        public void Serialize_Primitives_UsesJsonLiterals()
        {
            Assert.Equal("3", CesiumJson.Serialize(3));
            Assert.Equal("true", CesiumJson.Serialize(true));
            Assert.Equal("null", CesiumJson.Serialize(null));
            Assert.Equal("\"text\"", CesiumJson.Serialize("text"));
        }

        [Fact]
        public void Serialize_FloatAndDouble_AreInvariantCulture()
        {
            Assert.Equal("1.5", CesiumJson.Serialize(1.5f));
            Assert.Equal("0.25", CesiumJson.Serialize(0.25d));
        }

        [Fact]
        public void Serialize_Enum_WritesName()
        {
            Assert.Equal("\"Warn\"", CesiumJson.Serialize(SdkLogLevel.Warn));
        }

        [Fact]
        public void Serialize_EscapesControlCharsAndQuotes()
        {
            string json = CesiumJson.Serialize("a\"b\\c\nd\te");

            Assert.Equal("\"a\\\"b\\\\c\\nd\\te\"", json);
            // 序列化结果本身必须是可解析的
            Assert.Equal("a\"b\\c\nd\te", CesiumJson.Deserialize(json));
        }

        [Fact]
        public void Serialize_Object_IncludesPublicFieldsAndSkipsIgnored()
        {
            var dict = CesiumJson.ToDictionary(new Sample());

            Assert.True(dict.ContainsKey("Count"));
            Assert.True(dict.ContainsKey("Numbers"));
            Assert.True(dict.ContainsKey("Extra"));
            Assert.False(dict.ContainsKey("Doubled"));   // [CesiumJsonIgnore]
        }

        [Fact]
        public void Serialize_ThenDeserialize_RoundTripsNestedValues()
        {
            string json = CesiumJson.Serialize(new Nested());

            var root = CesiumJson.Deserialize(json) as Dictionary<string, object>;
            Assert.NotNull(root);
            Assert.Equal("outer", root["Name"]);

            var inner = root["Inner"] as Dictionary<string, object>;
            Assert.NotNull(inner);
            Assert.Equal(3d, inner["Count"]);      // 数字统一回读为 double
            Assert.Equal(true, inner["Enabled"]);
            Assert.Equal("he said \"hi\"\n\t\\end", inner["Label"]);

            var numbers = inner["Numbers"] as List<object>;
            Assert.NotNull(numbers);
            Assert.Equal(3, numbers.Count);
        }

        [Fact]
        public void SerializePretty_IsIndentedAndStillParseable()
        {
            string pretty = CesiumJson.SerializePretty(new Sample());

            Assert.Contains("\n", pretty);
            var parsed = CesiumJson.Deserialize(pretty) as Dictionary<string, object>;
            Assert.NotNull(parsed);
            Assert.Equal(3d, parsed["Count"]);
        }

        [Fact]
        public void TryDeserialize_BrokenInput_ReturnsFalseNotThrow()
        {
            object value;

            Assert.False(CesiumJson.TryDeserialize(null, out value));
            Assert.False(CesiumJson.TryDeserialize("", out value));
            Assert.False(CesiumJson.TryDeserialize("{not json", out value));
            Assert.False(CesiumJson.TryDeserialize("{\"a\":}", out value));
            Assert.Null(CesiumJson.Deserialize("{not json"));
        }

        [Fact]
        public void Deserialize_WhitespaceAndNesting_AreAccepted()
        {
            var value = CesiumJson.Deserialize(" \n { \"a\" : [ 1 , 2 , { \"b\" : false } ] } \t ") as Dictionary<string, object>;

            Assert.NotNull(value);
            var list = value["a"] as List<object>;
            Assert.NotNull(list);
            Assert.Equal(3, list.Count);
            Assert.Equal(1d, list[0]);
            Assert.Equal(false, ((Dictionary<string, object>)list[2])["b"]);
        }
    }
}
