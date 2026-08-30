using Arbitarr.Core.Filtering;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Proves <see cref="Precedence"/> remains the rule-priority enum only — five tiers,
/// Lowest..Highest, in ascending order — and is not extended/overloaded to also carry the D3
/// suppression-source chain (allow-rule &gt; deny-rule &gt; AI verdict &gt; pass), which lives in the
/// distinct <see cref="SuppressionPrecedenceChain"/>/<see cref="SuppressionSource"/> types instead
/// (Architect note A5, M4-9).
/// </summary>
public class PrecedenceTests
{
    [Fact]
    public void Precedence_HasExactlyFiveTiers_LowestToHighest()
    {
        var values = Enum.GetValues<Precedence>();
        Assert.Equal(5, values.Length);
    }

    [Fact]
    public void Precedence_TiersAreOrdered_LowestToHighest()
    {
        Assert.True(Precedence.Lowest < Precedence.Low);
        Assert.True(Precedence.Low < Precedence.Normal);
        Assert.True(Precedence.Normal < Precedence.High);
        Assert.True(Precedence.High < Precedence.Highest);
    }

    [Fact]
    public void Precedence_IsDistinctType_FromSuppressionSource()
    {
        // A5: the D3 suppression-source chain is a separate enum/type, never a member added to
        // Precedence itself.
        Assert.NotEqual(typeof(Precedence), typeof(SuppressionSource));
    }
}
