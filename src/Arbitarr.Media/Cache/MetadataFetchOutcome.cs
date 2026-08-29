namespace Arbitarr.Media.Cache;

/// <summary>
/// Which of the source's distinct outcomes a live fetch attempt produced, mirroring
/// <c>Arbitarr.Media.Providers.XemOutcomeKind</c> so <see cref="MetadataCacheCoordinator"/> can
/// stay provider-shape-agnostic (it depends on this small delegate-friendly type rather than
/// referencing a specific provider's result envelope).
/// </summary>
public enum MetadataFetchOutcomeKind
{
    /// <summary>The fetch succeeded and returned raw content.</summary>
    Success,

    /// <summary>The upstream source could not be reached (network failure, timeout, server error).</summary>
    Unreachable,

    /// <summary>The upstream source was reached but affirmatively reports no coverage for this key.</summary>
    NoCoverage,
}

/// <summary>
/// Outcome of a single live fetch attempt against an upstream metadata source, as reported by the
/// caller (typically a provider such as <c>XemProvider</c>) to <see cref="MetadataCacheCoordinator"/>.
/// </summary>
/// <param name="Kind">Which distinct outcome the fetch produced.</param>
/// <param name="RawContent">The raw fetched content, present only when <see cref="Kind"/> is <see cref="MetadataFetchOutcomeKind.Success"/>.</param>
public sealed record MetadataFetchOutcome(MetadataFetchOutcomeKind Kind, string? RawContent)
{
    public static MetadataFetchOutcome Success(string rawContent) => new(MetadataFetchOutcomeKind.Success, rawContent);

    public static MetadataFetchOutcome Unreachable() => new(MetadataFetchOutcomeKind.Unreachable, null);

    public static MetadataFetchOutcome NoCoverage() => new(MetadataFetchOutcomeKind.NoCoverage, null);
}
