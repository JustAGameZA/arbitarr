namespace Arbitarr.Core.Filtering;

/// <summary>
/// Relative ordering used when multiple filter rules could apply to the same release.
/// </summary>
public enum Precedence
{
    Lowest = 0,
    Low = 1,
    Normal = 2,
    High = 3,
    Highest = 4,
}
