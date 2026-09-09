using System.Net.Http;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data.Media;
using Microsoft.Extensions.Caching.Memory;

namespace Arbitarr.Media.Providers;

/// <summary>
/// arb-u1c: resolves a provider id to the series title the search path sends upstream, by asking
/// the *arr instance that sent the request.
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
/// <b>ONE TIER, NOT TWO.</b> ADR 0002's preference order puts <see cref="AnimeListsProvider"/>
/// behind <see cref="ArrApiProvider"/> as a fallback, and an earlier revision of this type wired
/// it. That tier is NOT wired here, deliberately: <see cref="AnimeListsProvider"/> is registered
/// nowhere, and registering it is not a composition-root line — it has no source URL anywhere in
/// the repository, so wiring it means choosing a third-party upstream, giving it an operator-facing
/// configuration knob, and admitting a first-run network fetch onto the search path. Shipping the
/// tier with an optional constructor parameter that DI leaves null was worse than not shipping it:
/// the fallback never ran in production while four tests passed by constructing it directly, which
/// is a test suite asserting about code no request reaches. See bead arb-5uw. Until that lands, an
/// unresolved title degrades to the id-only upstream request, exactly as it did before.
/// </para>
/// <para>
/// <b>THE CREDENTIAL IS NOT READ HERE.</b> It comes from
/// <see cref="SonarrCredentialProvider"/>, which is the single production caller of
/// <c>ArrInstanceRepository.ReadApiKeyForUpstreamRequestAsync</c> (CLAUDE.md §1). The admin
/// connectivity probe needs the same credential, and having both sites call the reader is exactly
/// the call-site count that guarantee is made of.
/// </para>
/// <para>
/// <b>CONFIGURATION IS READ PER CALL, NOT CAPTURED AT STARTUP.</b> The Sonarr address and key live
/// in the database and can be changed from the admin UI at any time, so an
/// <see cref="ArrApiProvider"/> built once at registration would keep using a stale address (or
/// none at all, if the instance was configured after boot) for the life of the process. Building it
/// per call costs one settings read against SQLite on a path that is about to make a network
/// request anyway — and on the hot path it usually costs nothing at all, because
/// <see cref="ResolveAsync"/> answers from the memo before reaching it.
/// </para>
/// </remarks>
public sealed class SeriesTitleResolver : IIdentityResolver
{
    /// <summary>The named <see cref="HttpClient"/> the *arr lookup is issued on.</summary>
    /// <remarks>
    /// Named rather than typed because <see cref="ArrApiProvider"/> is constructed per call with
    /// configuration read from the database, so it cannot be a DI-activated typed client. The
    /// registration in <c>Program.cs</c> carries the SSRF, timeout and logging notes that apply to
    /// it — including that the client's TIMEOUT IS SET THERE, once, and must never be assigned per
    /// call (see <see cref="ArrApiProvider"/>).
    /// </remarks>
    public const string ArrHttpClientName = "ArrIdentityLookup";

    /// <summary>
    /// The wall-clock budget one identity lookup may spend before the search gives up on it.
    /// </summary>
    /// <remarks>
    /// <b>THIS IS A SEARCH-PATH BUDGET, NOT A CLIENT TIMEOUT.</b> The named client's timeout is
    /// sized for the admin probe's "is this address correct" question and is far too long here: a
    /// title is an OPTIMISATION of the upstream query, and a Sonarr that has gone away must not add
    /// its whole timeout to every anime search while it fails. Two seconds is long enough for a
    /// healthy Sonarr on a LAN to answer one indexed lookup and short enough to disappear inside
    /// the upstream fan-out that follows. Imposed with a linked token at the call site rather than
    /// by mutating the pooled client, which is shared and cannot be re-timed per request.
    /// </remarks>
    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a resolved (or unresolvable) tvdbid-to-title answer is reused without asking Sonarr
    /// again.
    /// </summary>
    /// <remarks>
    /// Sonarr's answer to "what is series X called" changes on the order of never; the reason this
    /// is not simply permanent is that a series can be renamed or removed and the process can be
    /// long-lived. Five minutes collapses the burst that matters — one *arr search issues several
    /// requests for one series in quick succession, and pagination reissues them — without holding
    /// a stale title long enough for anyone to notice. NEGATIVE answers are memoised too, and for
    /// the same reason with more force: an unconfigured or unreachable Sonarr would otherwise be
    /// asked, and time out, once per request forever.
    /// </remarks>
    public static readonly TimeSpan MemoTtl = TimeSpan.FromMinutes(5);

    private readonly SonarrCredentialProvider _credentials;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAsyncCircuitBreaker _circuitBreaker;
    private readonly IMemoryCache _memo;
    private readonly TimeSpan _lookupBudget;

    public SeriesTitleResolver(
        SonarrCredentialProvider credentials,
        IHttpClientFactory httpClientFactory,
        IAsyncCircuitBreaker circuitBreaker,
        IMemoryCache memo,
        TimeSpan? lookupBudget = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _memo = memo ?? throw new ArgumentNullException(nameof(memo));
        _lookupBudget = lookupBudget ?? LookupBudget;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see langword="null"/> — meaning "no title, degrade to the id-only upstream request" —
    /// for every unresolved case: no tvdbid hint, an unconfigured or half-configured Sonarr, a
    /// Sonarr that cannot be reached or does not track the series, and a Sonarr that answers with
    /// nothing usable. Null is not an error state here; it reproduces exactly the behaviour that
    /// shipped before this resolver existed.
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

        // The memo is checked BEFORE anything else, including the settings read: a repeated search
        // for the same series must cost neither a database round trip nor a network call. A cached
        // null is a real answer (see MemoTtl), which is why TryGetValue's own result decides
        // whether to ask, rather than the nullness of the value it produced.
        if (_memo.TryGetValue(MemoKey(tvdbId), out string? memoised))
        {
            return BuildIdentity(tvdbId, memoised);
        }

        var resolved = await LookUpAsync(title, hints, tvdbId, cancellationToken).ConfigureAwait(false);

        // Not memoised when the CALLER cancelled: that is not an answer about this series, and
        // caching it would hand an aborted request's silence to the next one.
        if (!cancellationToken.IsCancellationRequested)
        {
            _memo.Set(MemoKey(tvdbId), resolved, MemoTtl);
        }

        return BuildIdentity(tvdbId, resolved);
    }

    private async Task<string?> LookUpAsync(
        string title,
        IdentityResolutionHints hints,
        int tvdbId,
        CancellationToken cancellationToken)
    {
        var credential = await _credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            // Nothing configured, or an address with no key: a half-configured instance would
            // answer /api/v3/episode with 401, which the provider records as a circuit-breaker
            // failure against a source that is not actually broken. Not asking is both cheaper and
            // honest about what is missing.
            return null;
        }

        var provider = new ArrApiProvider(
            new ArrApiProviderOptions(credential.BaseUrl, credential.ApiKey),
            _httpClientFactory.CreateClient(ArrHttpClientName),
            _circuitBreaker);

        // The budget rides on a linked token, so a slow Sonarr is abandoned by US rather than by the
        // pooled client's much longer timeout. Cancellation the caller requested still propagates.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_lookupBudget);

        SeriesIdentity? identity;
        try
        {
            identity = await provider.ResolveAsync(title, hints, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own budget fired. A search must still answer, so this degrades to "no title"
            // exactly as an unreachable Sonarr does.
            return null;
        }

        if (identity is null || !IsUsableTitle(identity.PrimaryTitle))
        {
            return null;
        }

        // THE ECHO GUARD. ArrApiProvider falls back to the title it was GIVEN when *arr returns
        // episodes carrying no series object, and here that argument is the caller's raw q — a bare
        // episode number, for the very shape this resolver exists to fix — so echoing it would send
        // "92 92" upstream.
        //
        // Compared NUMERICALLY when both sides are numeric, not only as text: "092" and "92" are the
        // same episode number differently spelled, and a text-only comparison lets the padded form
        // through to produce "92 092". Everything else falls through to the text comparison.
        if (IsSameNumber(identity.PrimaryTitle, title) || IsSameText(identity.PrimaryTitle, title))
        {
            return null;
        }

        // A numeric title that is NOT the number we sent is kept, deliberately. Rejecting every
        // all-digit title was tried and is wrong: "86" is a real series, and so are "91 Days" and
        // "5". The failure this guard is really about is DUPLICATING the number already going up,
        // which the comparison above catches exactly — and ArrApiProvider can only ever return the
        // title it was given or a genuine series.title from *arr, so "some other number" is not a
        // shape it can produce. Guarding against it anyway would silently make those series
        // unsearchable, which is a worse and quieter bug than the one being prevented.
        return identity.PrimaryTitle;
    }

    private static SeriesIdentity? BuildIdentity(int tvdbId, string? primaryTitle) =>
        primaryTitle is null
            ? null
            : new SeriesIdentity(tvdbId, TmdbId: null, primaryTitle, Array.Empty<string>());

    /// <summary>
    /// Namespaced so this resolver's entries cannot collide with another feature's in a shared
    /// <see cref="IMemoryCache"/>.
    /// </summary>
    private static string MemoKey(int tvdbId) => $"arb-u1c:series-title:{tvdbId}";

    private static bool IsUsableTitle(string? candidate) => !string.IsNullOrWhiteSpace(candidate);

    /// <summary>
    /// Whether both sides are episode numbers and are the SAME number — so a zero-padded echo is
    /// caught as the duplicate it is rather than passing a text comparison.
    /// </summary>
    private static bool IsSameNumber(string left, string right) =>
        TryParseEpisodeNumber(left, out var leftNumber)
        && TryParseEpisodeNumber(right, out var rightNumber)
        && leftNumber == rightNumber;

    private static bool TryParseEpisodeNumber(string candidate, out int value)
    {
        var trimmed = candidate.Trim();
        value = 0;
        return trimmed.Length > 0
            && trimmed.All(char.IsAsciiDigit)
            && int.TryParse(
                trimmed,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }

    private static bool IsSameText(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
