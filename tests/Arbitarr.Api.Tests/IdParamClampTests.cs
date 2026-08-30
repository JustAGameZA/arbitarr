using Arbitarr.Api.Search;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Security-m3 MEDIUM #4: <see cref="IdParamClamp"/> bounds attacker/client-controlled
/// <c>tvdbid</c>/<c>tmdbid</c>/<c>season</c>/<c>ep</c> query-string inputs at the endpoint
/// boundary, independent of what any downstream component does with them.
/// </summary>
public class IdParamClampTests
{
    [Fact]
    public void ClampProviderId_WhenNull_StaysNull()
    {
        Assert.Null(IdParamClamp.ClampProviderId(null));
    }

    [Fact]
    public void ClampProviderId_WhenZeroOrNegative_BecomesNull()
    {
        Assert.Null(IdParamClamp.ClampProviderId(0));
        Assert.Null(IdParamClamp.ClampProviderId(-1));
        Assert.Null(IdParamClamp.ClampProviderId(int.MinValue));
    }

    [Fact]
    public void ClampProviderId_WhenAboveMaximum_BecomesNull()
    {
        Assert.Null(IdParamClamp.ClampProviderId(IdParamClamp.MaxProviderId + 1));
        Assert.Null(IdParamClamp.ClampProviderId(int.MaxValue));
    }

    [Fact]
    public void ClampProviderId_WhenWithinRange_PassesThroughUnchanged()
    {
        Assert.Equal(1, IdParamClamp.ClampProviderId(1));
        Assert.Equal(74796, IdParamClamp.ClampProviderId(74796));
        Assert.Equal(IdParamClamp.MaxProviderId, IdParamClamp.ClampProviderId(IdParamClamp.MaxProviderId));
    }

    [Fact]
    public void ClampSeason_WhenNull_StaysNull()
    {
        Assert.Null(IdParamClamp.ClampSeason(null));
    }

    [Fact]
    public void ClampSeason_WhenNegative_BecomesNull()
    {
        Assert.Null(IdParamClamp.ClampSeason(-1));
        Assert.Null(IdParamClamp.ClampSeason(int.MinValue));
    }

    [Fact]
    public void ClampSeason_WhenAboveMaximum_BecomesNull()
    {
        Assert.Null(IdParamClamp.ClampSeason(IdParamClamp.MaxSeason + 1));
        Assert.Null(IdParamClamp.ClampSeason(int.MaxValue));
    }

    [Fact]
    public void ClampSeason_WhenWithinRange_PassesThroughUnchanged()
    {
        Assert.Equal(0, IdParamClamp.ClampSeason(0));
        Assert.Equal(17, IdParamClamp.ClampSeason(17));
        Assert.Equal(IdParamClamp.MaxSeason, IdParamClamp.ClampSeason(IdParamClamp.MaxSeason));
    }

    [Fact]
    public void ClampEpisode_WhenNull_StaysNull()
    {
        Assert.Null(IdParamClamp.ClampEpisode(null));
    }

    [Fact]
    public void ClampEpisode_WhenNegative_BecomesNull()
    {
        Assert.Null(IdParamClamp.ClampEpisode(-1));
        Assert.Null(IdParamClamp.ClampEpisode(int.MinValue));
    }

    [Fact]
    public void ClampEpisode_WhenAboveMaximum_BecomesNull()
    {
        Assert.Null(IdParamClamp.ClampEpisode(IdParamClamp.MaxEpisode + 1));
        Assert.Null(IdParamClamp.ClampEpisode(int.MaxValue));
    }

    [Fact]
    public void ClampEpisode_WhenWithinRange_PassesThroughUnchanged()
    {
        Assert.Equal(0, IdParamClamp.ClampEpisode(0));
        Assert.Equal(36, IdParamClamp.ClampEpisode(36));
        Assert.Equal(IdParamClamp.MaxEpisode, IdParamClamp.ClampEpisode(IdParamClamp.MaxEpisode));
    }
}
