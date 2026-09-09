using System.Net.Http;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data.Media;

namespace Arbitarr.Media.Providers;

/// <summary>
/// arb-u1c: resolves a provider id to the series title the search path sends upstream, in the Q5-D
/// preference order (<see cref="ArrApiProvider"/> first, <see cref="AnimeListsProvider"/> as
/// fallback).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS EXISTS AT ALL.</b> Sonarr's anime episode search sends
/// <c>t=tvsearch&amp;tvdbid=X&amp;q=NN</c>, where <c>NN</c> is the bare ABSOLUTE episode number.
/// NZBHydra2 treats a present <c>q</c> as the whole query and, for indexers with no id support,
/// drops the id and searches the feed for that number alone — so <c>q=92</c> returns episode 92 of
/// every anime it can see. Withholding the number (the interim fix, #113/#114) finds the right
/// series but no longer the right episode. Only a TITLE turns the number back into a query an
/// indexer can execute: <c>One Piece 92</c>. That title has to come from somewhere, and the *arr
/// instance that sent the request is the one authority that already knows it.
/// </para>
/// <para>
/// <b>WHY IT IS AN <see cref="IIdentityResolver"/> RATHER THAN A NEW CONTRACT.</b> The interface
/// already returns a <see cref="SeriesIdentity"/> carrying <see cref="SeriesIdentity.PrimaryTitle"/>,
/// and <see cref="ArrApiProvider.ResolveAsync"/> already resolves it FROM
/// <see cref="IdentityResolutionHints.TvdbId"/> — this direction is not a new capability, it was
/// simply wired to nothing. Reusing the contract also keeps <c>Arbitarr.Api</c> free of any
/// reference to <c>Arbitarr.Media</c>: both projects see only <c>Arbitarr.Core.Identity</c>, and the
/// composition root is the single place that knows which implementation is in play (ADR 0001).
/// </para>
/// <para>
/// <b>CONFIGURATION IS READ PER CALL, NOT CAPTURED AT STARTUP.</b> The Sonarr address and key live
/// in <see cref="ArrInstanceRepository"/> and can be changed from the admin UI at any time, so an
/// <see cref="ArrApiProvider"/> built once at registration would keep using a stale address (or none
/// at all, if the instance was configured after boot) for the life of the process. Building it per
/// call costs one settings read against SQLite on a path that is about to make a network request
/// anyway.
/// </para>
/// </remarks>
public sealed class SeriesTitleResolver : IIdentityResolver
{
    /// <summary>The named <see cref="HttpClient"/> the *arr lookup is issued on.</summary>
    /// <remarks>
    /// Named rather than typed because <see cref="ArrApiProvider"/> is constructed per call with
    /// configuration read from the database, so it cannot be a DI-activated typed client. The
    /// registration in <c>Program.cs</c> carries the SSRF and logging notes that apply to it.
    /// </remarks>
    public const string ArrHttpClientName = "ArrIdentityLookup";

    private readonly ArrInstanceRepository _arrInstances;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAsyncCircuitBreaker _circuitBreaker;
    private readonly AnimeListsProvider? _animeLists;

    public SeriesTitleResolver(
        ArrInstanceRepository arrInstances,
        IHttpClientFactory httpClientFactory,
        IAsyncCircuitBreaker circuitBreaker,
        AnimeListsProvider? animeLists = null)
    {
        _arrInstances = arrInstances ?? throw new ArgumentNullException(nameof(arrInstances));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _animeLists = animeLists;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see langword="null"/> — meaning "no title, degrade to the id-only upstream request" —
    /// for every unresolved case: no tvdbid hint, an unconfigured or half-configured Sonarr, a
    /// Sonarr that cannot be reached or does not track the series, no AnimeLists coverage, and (per
    /// ADR 0002) an AnimeLists entry whose names cannot be separated. Null is not an error state
    /// here; it reproduces exactly the behaviour that shipped before this resolver existed.
    /// </remarks>
    public async Task<SeriesIdentity?> ResolveAsync(
        string title,
        IdentityResolutionHints hints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hints);

        if (hints.TvdbId is not { } tvdbId)
        {
            return null;
        }

        var fromArr = await TryResolveFromArrAsync(title, hints, cancellationToken).ConfigureAwait(false);
        if (fromArr is not null)
        {
            return fromArr;
        }

        return await TryResolveFromAnimeListsAsync(tvdbId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SeriesIdentity?> TryResolveFromArrAsync(
        string title,
        IdentityResolutionHints hints,
        CancellationToken cancellationToken)
    {
        var baseUrl = await _arrInstances.GetBaseUrlAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        // The only caller of this reader outside the repository's own tests. It exists so the key can
        // reach the wire without ever being readable through an admin surface; do not add another
        // caller, and do not log the value.
        var apiKey = await _arrInstances.ReadApiKeyForUpstreamRequestAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // A base URL with no key is a half-configured instance: /api/v3/episode would answer 401,
            // which the provider records as a circuit-breaker failure against a source that is not
            // actually broken. Not asking is both cheaper and honest about what is missing.
            return null;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl))
        {
            return null;
        }

        var provider = new ArrApiProvider(
            new ArrApiProviderOptions(parsedBaseUrl, apiKey),
            _httpClientFactory.CreateClient(ArrHttpClientName),
            _circuitBreaker);

        var identity = await provider.ResolveAsync(title, hints, cancellationToken).ConfigureAwait(false);

        // ArrApiProvider echoes back the title it was GIVEN when *arr returns episodes carrying no
        // series object. Here that argument is the caller's raw q — a bare episode number, for the
        // very shape this resolver exists to fix — so echoing it would send "92 92" upstream. Admit
        // nothing instead and let the fallback try.
        if (identity is null || !IsUsableTitle(identity.PrimaryTitle) || IsSameText(identity.PrimaryTitle, title))
        {
            return null;
        }

        return identity;
    }

    private async Task<SeriesIdentity?> TryResolveFromAnimeListsAsync(int tvdbId, CancellationToken cancellationToken)
    {
        if (_animeLists is null)
        {
            return null;
        }

        var result = await _animeLists.GetByTvdbIdAsync(tvdbId, cancellationToken).ConfigureAwait(false);
        if (result.Kind != AnimeListsOutcomeKind.Success || result.Value is not { } entry)
        {
            return null;
        }

        // ADR 0002, and the reason this is NOT "take the first name". AnimeListsEntry.Names is an
        // unordered set of alternate renderings with no primary designation — document order is an
        // artefact of how the XML was hand-edited, not a ranking. Several DISTINCT names therefore
        // means the data cannot say which one to search for, and picking one anyway would send a
        // confidently wrong query upstream. Admitting no title is the correct answer: the caller
        // degrades to the id-only request, which is imprecise but never wrong.
        var distinct = entry.Names
            .Where(IsUsableTitle)
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count != 1)
        {
            return null;
        }

        return new SeriesIdentity(tvdbId, entry.TmdbId, distinct[0], Array.Empty<string>());
    }

    private static bool IsUsableTitle(string? candidate) => !string.IsNullOrWhiteSpace(candidate);

    private static bool IsSameText(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
