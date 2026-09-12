namespace Arbitarr.Media.Providers;

/// <summary>
/// Configuration for <see cref="AnimeListsProvider"/>.
/// </summary>
/// <param name="SourceUrl">
/// URL of AniDB's <c>anime-lists</c> static XML mapping, e.g.
/// <c>https://anime-lists.example/anime-list-full.xml</c>. Fetched at runtime; never vendored into
/// the repo (AC21).
///
/// <para><b>arb-5uw: NULL IS A REAL STATE, NOT A MISSING VALUE.</b> There is deliberately NO default
/// here and no upstream named anywhere in the repository: choosing which third-party mapping to
/// fetch — and its licence — is the operator's decision, and a committed default would make that
/// choice for them AND admit a first-run network fetch onto the search path nobody opted into. Null
/// therefore means the AnimeLists tier is registered but INACTIVE: <see cref="AnimeListsProvider"/>
/// reports <see cref="AnimeListsOutcomeKind.NotConfigured"/> without touching the network or the
/// filesystem. Operators set <c>Arbitarr:AnimeLists:SourceUrl</c> to opt in.</para>
/// </param>
/// <param name="ConfigDirectory">
/// Directory the fetched XML is written into and read back from (AC21: "into <c>/config</c>", made
/// configurable here so tests and non-container deployments can point elsewhere). Defaults to
/// <c>/config</c>.
/// </param>
/// <param name="FileName">File name the XML is persisted under within <see cref="ConfigDirectory"/>.</param>
/// <param name="MinimumRefetchInterval">
/// AC19: never re-fetch the same dataset within this interval. Defaults to 24 hours, checked against
/// the persisted file's last-write timestamp — no separate freshness store is kept.
/// </param>
/// <param name="MinimumRequestSpacing">
/// AC19: AniDB-related fetch etiquette rate limit — at most one request per this interval. Defaults
/// to 2 seconds.
/// </param>
/// <param name="RequestTimeout">
/// Per-HTTP-request timeout for the runtime fetch. Defaults to 30s given the file's size. Applied by
/// whoever OWNS the <see cref="HttpClient"/> — the named-client registration in <c>Program.cs</c> in
/// production — never by <see cref="AnimeListsProvider"/>'s constructor; see
/// <see cref="AnimeListsProvider"/>'s remarks for why that assignment cannot live there.
/// </param>
public sealed record AnimeListsProviderOptions(
    Uri? SourceUrl,
    string ConfigDirectory = "/config",
    string FileName = "anime-list-full.xml",
    TimeSpan? MinimumRefetchInterval = null,
    TimeSpan? MinimumRequestSpacing = null,
    TimeSpan? RequestTimeout = null)
{
    public TimeSpan EffectiveMinimumRefetchInterval => MinimumRefetchInterval ?? TimeSpan.FromHours(24);

    public TimeSpan EffectiveMinimumRequestSpacing => MinimumRequestSpacing ?? TimeSpan.FromSeconds(2);

    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(30);

    public string FilePath => Path.Combine(ConfigDirectory, FileName);
}
