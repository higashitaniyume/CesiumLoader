using System.Linq;
using FontMod;
using Xunit;

namespace FontMod.Tests;

public sealed class FontNamesTests
{
    [Fact]
    public void BuildCandidates_PutsChosenFontFirst_AndDedupesIgnoringCaseAndSpaces()
    {
        var names = FontNames.BuildCandidates("Microsoft YaHei", " simhei , MICROSOFTYAHEI ,SimSun");

        Assert.Equal("Microsoft YaHei", names[0]);
        Assert.Single(names.Where(n => FontNames.Normalize(n) == "microsoftyahei"));
        Assert.Contains(names, n => FontNames.Normalize(n) == "simhei");
        Assert.Contains(names, n => FontNames.Normalize(n) == "simsun");
    }

    [Fact]
    public void BuildCandidates_AppendsBuiltInFallbacks()
    {
        var names = FontNames.BuildCandidates("Some Latin Font", "");

        Assert.Equal("Some Latin Font", names[0]);
        Assert.Contains("Microsoft YaHei", names);
        Assert.Contains("Microsoft JhengHei", names);
    }

    [Fact]
    public void BuildCandidates_EmptyFamily_StillReturnsFallbacks()
    {
        var names = FontNames.BuildCandidates("", "");

        Assert.NotEmpty(names);
        Assert.Contains("Microsoft YaHei", names);
    }

    [Fact]
    public void Contains_IgnoresCaseAndSpaces()
    {
        var installed = new[] { "Microsoft YaHei", "SimHei" };

        Assert.True(FontNames.Contains(installed, "microsoftyahei"));
        Assert.True(FontNames.Contains(installed, " SimHei "));
        Assert.False(FontNames.Contains(installed, "SimSun"));
    }

    [Fact]
    public void Contains_DoesNotBlockWhenListUnavailable()
    {
        Assert.True(FontNames.Contains(null, "SimSun"));
        Assert.True(FontNames.Contains(new string[0], "SimSun"));
    }
}
