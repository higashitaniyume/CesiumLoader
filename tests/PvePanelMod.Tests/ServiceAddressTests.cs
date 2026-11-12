// 来自 hpenguins/HandViewerMod 的 tests/HandViewerMod.Tests/ServiceAddressTests.cs（AGPL-3.0，原作者 hpenguins），改成测试 PVE 面板里改写后的版本。
using System;
using PvePanelMod.Hands;
using Xunit;

namespace PvePanelMod.Tests
{
    public class ServiceAddressTests
    {
        [Theory]
        [InlineData("http://192.168.31.2:18743/", "http://192.168.31.2:18743/query?id=a%26b")]
        [InlineData("https://example.com", "https://example.com/query?id=a%26b")]
        [InlineData("https://example.com/api/", "https://example.com/api/query?id=a%26b")]
        public void SupportsHttpHttpsAndEscapesCode(string address, string expected)
            => Assert.Equal(expected, ServiceAddress.QueryUrl(address, "a&b"));

        [Theory]
        [InlineData("file:///C:/test")]
        [InlineData("ftp://example.com")]
        [InlineData("http://user:password@example.com")]
        [InlineData("http://example.com/?token=secret")]
        [InlineData("http://example.com/#fragment")]
        [InlineData("not a URL")]
        public void RejectsUnsafeOrInvalidAddresses(string address)
            => Assert.Throws<ArgumentException>(() => ServiceAddress.QueryUrl(address, "test"));
    }
}
