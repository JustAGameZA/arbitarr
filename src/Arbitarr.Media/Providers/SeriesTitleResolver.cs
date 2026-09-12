using System.Net.Http;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Media;
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
/// <b>arb-5uw: TWO TIERS, THE SECOND INACTIVE UNTIL AN OPERATOR OPTS IN.</b> Per ADR 0002's
/// preference order, <see cref="ArrApiProvider"/> answers first — it is the *arr instance's own
/// authoritative data — and <see cref="AnimeListsProvider"/> is consulted only when it does not.
/// The provider is a REQUIRED constructor dependency, not an optional one: an earlier revision made
/// it optional, DI passed null because it was registered nowhere, and the tier was dead in
/// production while four tests passed by constructing it directly. Required means a missing
/// registration fails at STARTUP, where it is visible, instead of silently disabling a tier.
/// </para>
/// <para>Registered does not mean active. <see cref="AnimeListsProviderOptions.SourceUrl"/>
/// (<c>Arbitarr:AnimeLists:SourceUrl</c>) has no default anywhere in the repository, because
/// choosing the third-party upstream and its licence is the operator's decision and a committed
/// default would also put a first-run network fetch on the search path nobody asked for. Unset, the
/// provider reports <see cref="AnimeListsOutcomeKind.NotConfigured"/> with no I/O at all, and this
/// tier contributes nothing — an unresolved title degrades to the id-only upstream request exactly
/// as it did before.</para>
///
/// <para>ADR 0002 governs what the tier may admit: several DISTINCT names for one series cannot be
/// separated, so NONE is admitted and
/// <see cref="MatchProvenanceFlags.AmbiguousMapping"/> is reported through
/// <see cref="LastAnimeListsFlags"/> — an inspectable flag rather than a null the caller has to
/// interpret, which is the whole point of that decision. Exactly one name resolves.</para>
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
/// <para>
/// <b>arb-iiy: THE MEMO IS KEYED ON THE INSTANCE AS WELL AS THE SERIES.</b> Because the memo is
/// checked before the settings read, a repoint would otherwise keep serving the PREVIOUS server's
/// title for the rest of <see cref="MemoTtl"/>. The key therefore carries
/// <see cref="IArrInstanceEpoch.Current"/>, which <c>ArrInstanceRepository.SetAsync</c> advances
/// after a successful write — an in-memory read, so the hit path stays free of the database round
/// trip that folding the address itself into the key would have required. Entries written under an
/// earlier epoch are not swept; they become unreachable and expire on their own TTL.
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
    /// How long a RESOLVED tvdbid-to-title answer is reused without asking Sonarr again.
    /// </summary>
    /// <remarks>
    /// Sonarr's answer to "what is series X called" changes on the order of never; the reason this
    /// is not simply permanent is that a series can be renamed or removed and the process can be
    /// long-lived. Five minutes collapses the burst that matters — one *arr search issues several
    /// requests for one series in quick succession, and pagination reissues them — without holding
    /// a stale title long enough for anyone to notice.
    /// </remarks>
    public static readonly TimeSpan MemoTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a NEGATIVE answer — no title, from an unconfigured Sonarr, a timeout, or an error —
    /// is reused before asking again. Deliberately far shorter than <see cref="MemoTtl"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>WHY THE TWO TTLs ARE NOT ONE.</b> A negative answer is cached for a different reason
    /// than a positive one, so it earns a different lifetime. Caching it at all is necessary: an
    /// unconfigured or unreachable Sonarr would otherwise be asked — and time out against
    /// <see cref="LookupBudget"/> — once per request forever. But a negative answer is far likelier
    /// to be WRONG BY THE TIME IT IS REUSED: "this series is called One Piece" stays true for years,
    /// while "Sonarr did not answer" describes a moment, and the operator who has just finished
    /// configuring Sonarr is actively trying to change it.
    /// </para>
    ///
    /// <para><b>THE COST A LONG NEGATIVE TTL WOULD IMPOSE, which is what sets this value.</b> The
    /// resolved title is part of the snapshot token (see
    /// <c>PaginationSnapshotService.ComputeSnapshotToken</c>), so an unresolved lookup materialises
    /// a SEPARATE id-only snapshot that then lives for the snapshot TTL. Pinning the negative answer
    /// for five minutes would hold searches on that unresolved variant for the whole memo window ON
    /// TOP OF the snapshot's own TTL, so a single transient Sonarr blip could degrade anime searches
    /// for the better part of ten minutes. Forty-five seconds bounds the retry cost — a
    /// misconfigured Sonarr is asked at most a handful of times a minute, each capped by the lookup
    /// budget — while keeping the unresolved window short enough that the next snapshot
    /// materialisation picks the resolved title up.</para>
    /// </remarks>
    public static readonly TimeSpan NegativeMemoTtl = TimeSpan.FromSeconds(45);

    private readonly SonarrCredentialProvider _credentials;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAsyncCircuitBreaker _circuitBreaker;
    private readonly IMemoryCache _memo;
    private readonly IArrInstanceEpoch _epoch;
    private readonly AnimeListsProvider _animeLists;
    private readonly TimeSpan _lookupBudget;

    public SeriesTitleResolver(
        SonarrCredentialProvider credentials,
        IHttpClientFactory httpClientFactory,
        IAsyncCircuitBreaker circuitBreaker,
        IMemoryCache memo,
        IArrInstanceEpoch epoch,
        AnimeListsProvider animeLists,
        TimeSpan? lookupBudget = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _memo = memo ?? throw new ArgumentNullException(nameof(memo));
        _epoch = epoch ?? throw new ArgumentNullException(nameof(epoch));

        // REQUIRED, arb-5uw. Optional-with-a-null-default is exactly how this tier shipped dead
        // last time: DI had nothing to inject, the parameter defaulted to null, and the fallback
        // never ran while its tests passed by constructing the provider themselves. Throwing here
        // moves a missing registration to startup, where it is a failure rather than a silence.
        _animeLists = animeLists ?? throw new ArgumentNullException(nameof(animeLists));

        _lookupBudget = lookupBudget ?? LookupBudget;
    }

    /// <summary>
    /// ADR 0002: the flags the most recent AnimeLists-tier consultation produced —
    /// <see cref="MatchProvenanceFlags.AmbiguousMapping"/> when several distinct names claimed the
    /// series and none was admitted, <see cref="MatchProvenanceFlags.None"/> otherwise.
    /// </summary>
    /// <remarks>
    /// <para><b>WHY THIS IS NOT INFERRED FROM THE NULL.</b> ADR 0002 requires "ambiguous, so
    /// withheld" to be distinguishable from "nothing found", because they call for different
    /// responses; a caller reading only <see cref="ResolveAsync"/>'s null cannot tell them apart.
    /// <see cref="IIdentityResolver"/> carries no provenance channel, so the flag is surfaced here
    /// rather than by widening that contract for one implementation's tier.</para>
    ///
    /// <para>Not reset by a memo hit and not part of the memo key: it describes the last
    /// CONSULTATION, which is what an operator inspecting a withheld result wants to know.</para>
    /// </remarks>
    public MatchProvenanceFlags LastAnimeListsFlags { get; private set; } = MatchProvenanceFlags.None;

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
        // null is a real answer (see NegativeMemoTtl), which is why TryGetValue's own result decides
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
            // A resolved title and a failure to resolve get DIFFERENT lifetimes, and the difference
            // is load-bearing rather than a tuning preference: an unresolved answer also selects a
            // separate snapshot variant, so holding it as long as a positive one would stack the
            // memo window on top of the snapshot TTL. See NegativeMemoTtl.
            _memo.Set(MemoKey(tvdbId), resolved, resolved is null ? NegativeMemoTtl : MemoTtl);
        }

        return BuildIdentity(tvdbId, resolved);
    }

    /// <summary>
    /// ADR 0002's preference order for one lookup: the *arr instance's own API first, then the
    /// AnimeLists tier, then nothing.
    /// </summary>
    /// <remarks>
    /// The second tier runs only when the FIRST ADMITTED NOTHING — a title from *arr is
    /// authoritative for the instance that sent the request, so there is no case where a static
    /// third-party map should override it, and consulting it anyway would spend a fetch (and a first
    /// run's whole download) on every search that already had its answer.
    /// </remarks>
    private async Task<string?> LookUpAsync(
        string title,
        IdentityResolutionHints hints,
        int tvdbId,
        CancellationToken cancellationToken)
    {
        var fromArrApi = await LookUpViaArrApiAsync(title, hints, cancellationToken).ConfigureAwait(false);
        if (fromArrApi is not null)
        {
            // The AnimeLists tier is NOT consulted: a resolved title ends the lookup, so its flags
            // from a previous consultation must not be left standing as if they described this one.
            LastAnimeListsFlags = MatchProvenanceFlags.None;
            return fromArrApi;
        }

        return await LookUpViaAnimeListsAsync(tvdbId, title, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR 0002's SECOND tier: AniDB's static anime-lists map, consulted only when the *arr instance
    /// admitted nothing, and only when an operator has configured a source URL.
    /// </summary>
    /// <remarks>
    /// <para><b>SEVERAL DISTINCT NAMES ADMIT NOTHING (ADR 0002).</b> An entry may carry more than
    /// one <c>&lt;name&gt;</c>, and when those are genuinely different series names there is no
    /// principled basis for preferring one: the map is hand-edited and unversioned, so "the first
    /// one" is wrong exactly as often as it is right. None is admitted and
    /// <see cref="LastAnimeListsFlags"/> records
    /// <see cref="MatchProvenanceFlags.AmbiguousMapping"/>, which is what makes the withholding
    /// inspectable instead of indistinguishable from a coverage gap.</para>
    ///
    /// <para>Distinctness is compared case-insensitively after trimming, so one name repeated in two
    /// spellings is one name rather than a manufactured ambiguity.</para>
    ///
    /// <para>The echo guard applies here for the same reason it applies to the *arr tier: the
    /// caller's <c>title</c> is a bare absolute episode number, and a name equal to it would put
    /// "92 92" on the wire.</para>
    /// </remarks>
    private async Task<string?> LookUpViaAnimeListsAsync(
        int tvdbId,
        string title,
        CancellationToken cancellationToken)
    {
        LastAnimeListsFlags = MatchProvenanceFlags.None;

        // Short-circuits inside the provider with no network call and no filesystem access when no
        // source URL is configured (arb-5uw), which is the stock state.
        if (!_animeLists.IsConfigured)
        {
            return null;
        }

        AnimeListsResult<AnimeListsEntry> result;
        try
        {
            result = await _animeLists.GetByTvdbIdAsync(tvdbId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (result.Kind != AnimeListsOutcomeKind.Success || result.Value is not { } entry)
        {
            return null;
        }

        var names = entry.Names
            .Where(IsUsableTitle)
            .Select(n => n.Trim())
            .Where(n => !IsSameNumber(n, title) && !IsSameText(n, title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count > 1)
        {
            LastAnimeListsFlags = MatchProvenanceFlags.AmbiguousMapping;
            return null;
        }

        return names.Count == 1 ? names[0] : null;
    }

    private async Task<string?> LookUpViaArrApiAsync(
        string title,
        IdentityResolutionHints hints,
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
    /// <see cref="IMemoryCache"/>, and carrying the *arr instance epoch so a repoint invalidates.
    /// </summary>
    /// <remarks>
    /// <para><b>arb-iiy: THE EPOCH COMPONENT.</b> A title is only true OF THE INSTANCE THAT ANSWERED
    /// IT. Keyed on the tvdbid alone, repointing Sonarr at a different server kept serving the
    /// previous server's title for the rest of <see cref="MemoTtl"/>. The epoch
    /// (<see cref="IArrInstanceEpoch"/>) advances whenever that configuration is written, so entries
    /// from the previous instance are no longer reachable under the new key.</para>
    ///
    /// <para>Reading it costs an in-memory field, NOT a settings read — which is the whole reason it
    /// is a counter rather than the address itself, and what keeps the memo-first path above free of
    /// the database round trip its comment forbids. Stale entries are not swept: they simply become
    /// unreachable and expire on their own TTL as before.</para>
    /// </remarks>
    private string MemoKey(int tvdbId) => $"arb-u1c:series-title:{_epoch.Current}:{tvdbId}";

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
