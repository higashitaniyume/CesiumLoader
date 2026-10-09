using System;
using HandViewerMod;
using Xunit;

namespace HandViewerMod.Tests
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
