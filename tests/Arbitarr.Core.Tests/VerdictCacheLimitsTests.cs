using Arbitarr.Core.Filtering;

namespace Arbitarr.Core.Tests;

public sealed class VerdictCacheLimitsTests
{
    [Fact]
    public void TruncateRewrittenTitle_Null_ReturnsNull()
    {
        Assert.Null(VerdictCacheLimits.TruncateRewrittenTitle(null));
    }

    [Fact]
    public void TruncateRewrittenTitle_AtLimit_ReturnsSameInstance()
    {
        var atLimit = new string('t', VerdictCacheLimits.MaxRewrittenTitleLength);

        Assert.Same(atLimit, VerdictCacheLimits.TruncateRewrittenTitle(atLimit));
    }

    [Fact]
    public void TruncateRewrittenTitle_OverLimit_ReturnsPrefixOfLimitLength()
    {
        var over = new string('t', VerdictCacheLimits.MaxRewrittenTitleLength) + "overflow";

        var result = VerdictCacheLimits.TruncateRewrittenTitle(over);

        Assert.NotNull(result);
        Assert.Equal(VerdictCacheLimits.MaxRewrittenTitleLength, result.Length);
        Assert.StartsWith(result, over, StringComparison.Ordinal);
    }
}
