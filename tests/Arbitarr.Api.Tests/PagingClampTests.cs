using Arbitarr.Api.Search;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// SEC-M4: <see cref="PagingClamp"/> bounds attacker/client-controlled <c>limit</c>/<c>offset</c>
/// query-string inputs at the endpoint boundary, independent of what any downstream component does
/// with them.
/// </summary>
public class PagingClampTests
{
    [Fact]
    public void ClampLimit_WhenNull_DefaultsTo100()
    {
        Assert.Equal(100, PagingClamp.ClampLimit(null));
    }

    [Fact]
    public void ClampLimit_WhenBelowMinimum_ClampsToOne()
    {
        Assert.Equal(1, PagingClamp.ClampLimit(0));
        Assert.Equal(1, PagingClamp.ClampLimit(-50));
    }

    [Fact]
    public void ClampLimit_WhenAboveMaximum_ClampsToMaxLimit()
    {
        Assert.Equal(PagingClamp.MaxLimit, PagingClamp.ClampLimit(int.MaxValue));
        Assert.Equal(PagingClamp.MaxLimit, PagingClamp.ClampLimit(PagingClamp.MaxLimit + 1));
    }

    [Fact]
    public void ClampLimit_WhenWithinRange_PassesThroughUnchanged()
    {
        Assert.Equal(50, PagingClamp.ClampLimit(50));
        Assert.Equal(PagingClamp.MaxLimit, PagingClamp.ClampLimit(PagingClamp.MaxLimit));
        Assert.Equal(1, PagingClamp.ClampLimit(1));
    }

    [Fact]
    public void ClampOffset_WhenNull_DefaultsToZero()
    {
        Assert.Equal(0, PagingClamp.ClampOffset(null));
    }

    [Fact]
    public void ClampOffset_WhenNegative_ClampsToZero()
    {
        Assert.Equal(0, PagingClamp.ClampOffset(-1));
        Assert.Equal(0, PagingClamp.ClampOffset(int.MinValue));
    }

    [Fact]
    public void ClampOffset_WhenNonNegative_PassesThroughUnchanged()
    {
        Assert.Equal(0, PagingClamp.ClampOffset(0));
        Assert.Equal(500, PagingClamp.ClampOffset(500));
    }
}
