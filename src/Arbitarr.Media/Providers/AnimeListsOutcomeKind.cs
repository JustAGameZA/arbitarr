namespace Arbitarr.Media.Providers;

/// <summary>
/// Outcome of an <see cref="AnimeListsProvider"/> lookup, distinguishing the same degraded-state
/// shapes AC-M6 requires elsewhere in Step 3a: whether the dataset simply is not available locally
/// yet, whether the runtime fetch failed, or whether the dataset is available but has no entry for
/// the requested series.
/// </summary>
public enum AnimeListsOutcomeKind
{
    /// <summary>The call succeeded and data is present.</summary>
    Success,

    /// <summary>
    /// No usable dataset is available: nothing cached in <c>/config</c> yet and the runtime fetch
    /// could not populate it (network failure, timeout, non-success HTTP status).
    /// </summary>
    Unreachable,

    /// <summary>The dataset is available but has no entry for the requested series.</summary>
    NoCoverage,

    /// <summary>
    /// arb-5uw: no source URL is configured, so the tier is INACTIVE and no lookup was attempted.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Unreachable"/> on purpose, and the distinction is the same one AC-M6
    /// draws everywhere else in Step 3a: "nobody has opted this tier in" and "the tier is opted in
    /// but the upstream failed" call for completely different operator responses, and collapsing
    /// them would report a permanent, chosen configuration state as a transient outage. It also
    /// guarantees the property the registration rests on — reaching this outcome means NO network
    /// call and NO filesystem access happened at all.
    /// </remarks>
    NotConfigured,
}

/// <summary>
/// Result envelope for <see cref="AnimeListsProvider"/> calls, carrying either a successful payload
/// or one of the distinct degraded states above.
/// </summary>
/// <typeparam name="T">The payload type on success.</typeparam>
public sealed record AnimeListsResult<T>(AnimeListsOutcomeKind Kind, T? Value)
{
    public static AnimeListsResult<T> Success(T value) => new(AnimeListsOutcomeKind.Success, value);

    public static AnimeListsResult<T> Unreachable() => new(AnimeListsOutcomeKind.Unreachable, default);

    public static AnimeListsResult<T> NoCoverage() => new(AnimeListsOutcomeKind.NoCoverage, default);

    public static AnimeListsResult<T> NotConfigured() => new(AnimeListsOutcomeKind.NotConfigured, default);
}
