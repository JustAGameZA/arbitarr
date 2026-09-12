using Arbitarr.Core.Sources;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Arbitarr.Sources.Newznab;
using Arbitarr.Sources.NzbHydra;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Host.Sources;

/// <summary>
/// arb-x7w8.4: resolves the enabled <c>Sources</c> rows into the <see cref="IUpstreamSource"/>
/// instances a search fans out to — N of them, once per scope.
/// </summary>
/// <remarks>
/// <para><b>WHAT THIS REPLACES, AND WHY IT IS THE ACTUAL CHANGE.</b> Before this, Program.cs
/// registered exactly ONE <see cref="IUpstreamSource"/>, built from the single
/// <see cref="ResolvedSourceConfiguration"/> that <see cref="SourceSeeder"/> resolved at startup.
/// <c>UpstreamMergeStage</c> has always fanned out over an <c>IReadOnlyList</c> in parallel and
/// <c>CapsAggregator</c> has always merged caps across N sources, but neither had ever had more than
/// one source through it. This type is what makes that plumbing real, and it is also what makes
/// adding or removing an indexer take effect WITHOUT a restart: the rows are read per scope, so the
/// next request sees the operator's edit.</para>
///
/// <para><b>WHY IT LIVES IN <c>Arbitarr.Host</c>.</b> It constructs concrete adapters from
/// <c>Arbitarr.Sources.NzbHydra</c> and <c>Arbitarr.Sources.Newznab</c>, and AC6 reserves every
/// <c>Arbitarr.Sources.*</c> reference to <c>Arbitarr.Host</c>, the sole composition root (ADR
/// 0001). Its other dependencies — the <c>ArbitarrDbContext</c> and
/// <see cref="SourceCredentialProvider"/> — are in <c>Arbitarr.Data</c>, which Host already
/// references, so this is where the whole dependency set already meets.</para>
///
/// <para><b>SourceSeeder's ruling is untouched.</b> This reads the database and only the database;
/// the environment is not consulted here at any point, and must never become a fallback. See
/// <see cref="SourceSeeder"/>'s type doc for the 2026-09-07 incident that settled that.</para>
///
/// <para><b>EVERY SOURCE GETS ITS OWN <see cref="HttpClient"/>.</b> One named registration per
/// adapter kind is enough because <see cref="IHttpClientFactory.CreateClient"/> hands out a DISTINCT
/// <see cref="HttpClient"/> instance per call (only the underlying handler chain is pooled and
/// shared). That distinctness is load-bearing rather than incidental:
/// <see cref="NewznabSource"/>'s constructor honours the row's <c>TimeoutSeconds</c> by assigning
/// <see cref="HttpClient.Timeout"/> on the client it is given, so a client SHARED across sources
/// would silently make the last-constructed source's timeout win for all of them — and would throw
/// outright once a request had started on that instance. See NewznabSource:46-51, which states the
/// seam this must keep true.</para>
///
/// <para><b>An unknown <c>Kind</c> is skipped with a warning, never thrown.</b> Throwing would
/// discard every OTHER source in the set over one bad row — a single unrecognised kind (from a
/// downgrade, or a future kind written by a newer version) would take the whole search path down.
/// The warning names the row id and NOTHING ELSE, because everything else on the row is
/// operator-supplied text that could carry a credential into the log store.</para>
///
/// <para><b>Registered SCOPED, and the memo below is why that matters.</b> One resolution serves
/// every consumer in a request, so the merge stage and the caps aggregator share the same adapter
/// instances and the per-source keys are read once rather than once per consumer. The memo does not
/// outlive the scope, which is what keeps an operator's edit visible on the next request.</para>
/// </remarks>
public sealed class SourceRegistry : ISourceRegistry
{
    /// <summary>
    /// The named <see cref="HttpClient"/> registration direct Newznab/Torznab adapters are built
    /// over. Named rather than typed (<c>AddHttpClient&lt;NewznabSource&gt;</c>) because this type
    /// constructs its adapters itself — a typed client would try to have DI construct one, and
    /// <see cref="NewznabSource"/> takes options that only exist once a row has been read.
    /// </summary>
    public const string NewznabHttpClientName = "Arbitarr.Sources.Newznab";

    private readonly ArbitarrDbContext _dbContext;
    private readonly SourceCredentialProvider _credentials;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAsyncCircuitBreaker _circuitBreaker;
    private readonly ILogger<SourceRegistry> _logger;

    /// <summary>
    /// The resolution this scope has already performed, or null until the first one.
    /// </summary>
    /// <remarks>
    /// <para><b>WHAT THIS MEMOISES, AND WHAT IT DELIBERATELY DOES NOT.</b> It spans ONE SCOPE — one
    /// request — because this type is registered scoped. Two consumers in a request (the merge stage
    /// and the caps aggregator) then share one resolution rather than each reading every enabled row
    /// and every source's API key again. It is not a cache in any longer-lived sense: a new scope
    /// gets a new instance with an empty memo, which is exactly what makes an operator's edit visible
    /// on the very next request.</para>
    ///
    /// <para>No lock. A scoped service is resolved from a scope that is not shared across requests,
    /// and the consumers within one request await this sequentially at the top of their own work.
    /// Adding a lock here would suggest a concurrency this type does not have.</para>
    /// </remarks>
    private IReadOnlyList<IUpstreamSource>? _resolved;

    public SourceRegistry(
        ArbitarrDbContext dbContext,
        SourceCredentialProvider credentials,
        IHttpClientFactory httpClientFactory,
        IAsyncCircuitBreaker circuitBreaker,
        ILogger<SourceRegistry> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The enabled sources, highest <see cref="Source.Priority"/> first and <see cref="Source.Id"/>
    /// breaking ties, each as a live <see cref="IUpstreamSource"/>.
    /// </summary>
    /// <remarks>
    /// <para>An empty result is an ordinary answer, not a failure: an install with no sources
    /// configured — or with every source disabled — searches nothing and returns nothing, which is
    /// what the dashboard's empty state already tells the operator. Throwing here would turn that
    /// into a 500 on a correctly-unconfigured install.</para>
    ///
    /// <para><b>Ordered by priority descending because higher wins</b> (see
    /// <see cref="Source.Priority"/>), with the id as the tiebreak so the order is TOTAL and stable
    /// — two sources an operator left at the neutral default must not swap places between requests,
    /// which would make the dedup stage's "first member of an exact-match group" answer vary for the
    /// same data.</para>
    ///
    /// <para><b>The key is read per resolution and never captured.</b> It goes into the options
    /// record handed to the adapter that is about to use it; no field and no closure here holds a
    /// key, and the memo below holds the constructed adapters, not the credentials that built them.
    /// A key therefore never outlives the scope that needed it.</para>
    /// </remarks>
    public async Task<IReadOnlyList<IUpstreamSource>> ResolveAsync(CancellationToken cancellationToken)
    {
        // One resolution per scope. See the memo field: this is what stops two consumers in one
        // request each re-reading every enabled row and every source's key.
        if (_resolved is not null)
        {
            return _resolved;
        }

        var rows = await _dbContext.Sources
            .AsNoTracking()
            .Where(s => s.Enabled)
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var sources = new List<IUpstreamSource>(rows.Count);

        foreach (var row in rows)
        {
            var credential = await _credentials.GetAsync(row.Id, cancellationToken).ConfigureAwait(false);
            Add(sources, row, credential?.ApiKey);
        }

        return _resolved = sources;
    }


    /// <summary>
    /// Maps one row onto its adapter and appends it, or logs why it was skipped — the one place the
    /// kind mapping, the per-source client and the skip rules are expressed.
    /// </summary>
    private void Add(List<IUpstreamSource> sources, Source row, string? storedApiKey)
    {
        if (!Uri.TryCreate(row.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            // SourceRepository.ValidateBaseUrl rejects this at both write paths, so a row that
            // reaches here with an unparseable address predates that guard or was written around
            // it. Skipping one row beats failing the whole set, exactly as an unknown kind does.
            _logger.LogWarning(
                "Sources: source {SourceId} has a base URL that is not an absolute URL and was " +
                "skipped. The other sources are unaffected. The rejected value is deliberately " +
                "not shown here.",
                row.Id);
            return;
        }

        // Empty rather than null for a source with no stored key: both adapters take a non-nullable
        // string, and an unauthenticated request that the indexer answers 401 is the honest outcome
        // for a half-configured row — better than dropping the source, which would make a missing
        // key look identical to a source the operator never added.
        var apiKey = storedApiKey ?? string.Empty;

        switch (row.Kind)
        {
            case SourceRepository.NzbHydraKind:
                sources.Add(new NzbHydraSource(
                    new NzbHydraSourceOptions(
                        baseUrl,
                        apiKey,
                        row.DisplayName,
                        RequestTimeout: RequestTimeoutFor(row)),
                    CreateClient(nameof(NzbHydraSource)),
                    _circuitBreaker));
                break;

            case SourceRepository.NewznabKind:
            case SourceRepository.TorznabKind:
                // THE RESOLVED ENDPOINT MUST STAY ON THE ROW'S OWN ORIGIN. NewznabSource composes it
                // as `new Uri(BaseUrl, ApiPath.TrimStart('/'))`, and Uri composition lets an ABSOLUTE
                // second argument win outright — so an ApiPath of "http://elsewhere.example/x"
                // silently relocates every request, carrying this source's API key, to a host the
                // operator never configured. SourceRepository.ValidateApiPath does not close this:
                // it rejects a query string and an embedded apikey, and accepts everything else on
                // purpose (guessing at what a reverse proxy may serve is how a correct deployment
                // gets rejected).
                //
                // So the check is made on the RESOLVED endpoint rather than on the path's spelling,
                // which is what makes it hold against every escape form rather than the ones someone
                // thought to enumerate. arb-x7w8.20 is adding the same guard inside the adapter's
                // constructor; this one is here because the registry is a SECOND producer of
                // NewznabSourceOptions and a guard in only one producer closes only one door.
                if (!IsOnOrigin(baseUrl, row.ApiPath))
                {
                    _logger.LogWarning(
                        "Sources: source {SourceId} has an API path that resolves off its own base " +
                        "URL's origin and was skipped, because requests for it would carry its API " +
                        "key to a host it is not configured for. The other sources are unaffected. " +
                        "Neither value is shown here.",
                        row.Id);
                    return;
                }

                // ONE adapter for both kinds (arb-x7w8.2). They differ in what they return and in
                // how a download is served, neither of which is a branch this construction takes:
                // the endpoint arrives as the row's ApiPath and the attrs are handled by the
                // parser's namespace matching.
                sources.Add(new NewznabSource(
                    new NewznabSourceOptions(
                        baseUrl,
                        row.ApiPath,
                        apiKey,
                        row.DisplayName,
                        RequestTimeout: RequestTimeoutFor(row)),
                    CreateClient(NewznabHttpClientName),
                    _circuitBreaker));
                break;

            default:
                // THE ROW ID AND NOTHING ELSE. The kind is operator-supplied text, and so is the
                // base URL — which may still carry userinfo (SourceRepository.ValidateBaseUrl accepts
                // it today, unlike its *arr siblings; that is its own bead). Naming the id is enough
                // for an operator to find the row, and it is the one field here that cannot carry a
                // credential into the log store. LogMessageCleanser would not save this: it scrubs
                // credentials in query strings, not a bare value or a URL's userinfo (CLAUDE.md §1).
                _logger.LogWarning(
                    "Sources: source {SourceId} has a kind this version cannot resolve into a " +
                    "search source, and was skipped. The other sources are unaffected. The kind is " +
                    "deliberately not shown here; read it from the source row.",
                    row.Id);
                break;
        }
    }

    /// <summary>
    /// Whether <paramref name="apiPath"/>, composed against <paramref name="baseUrl"/> exactly as
    /// <see cref="NewznabSource"/> composes it, still lands on <paramref name="baseUrl"/>'s origin.
    /// </summary>
    /// <remarks>
    /// <para><b>THE CHECK IS ON THE RESOLVED URI, NOT ON THE PATH'S SPELLING, and that is the whole
    /// point.</b> A spelling check has to enumerate the forms that escape — an absolute URL, a
    /// scheme-relative <c>//host</c>, a backslash variant, a percent-encoded one — and it is wrong the
    /// first time one is missed. Composing the URI the way the adapter will and then comparing the
    /// ORIGIN cannot be missed the same way: whatever spelling produced an off-origin target, the
    /// target is what is compared.</para>
    ///
    /// <para>The composition is duplicated from <c>NewznabSource.EndpointUri</c> deliberately rather
    /// than shared: this must reproduce what that property DOES, so if the adapter's composition ever
    /// changes, the two disagreeing is the signal. A shared helper would keep them agreeing by
    /// construction while both were wrong.</para>
    ///
    /// <para>Scheme, host and port are all compared. Scheme matters on its own: the same host reached
    /// over http instead of https carries the key in clear, which is a downgrade rather than a
    /// redirection.</para>
    ///
    /// <para><b>Userinfo is refused too, and it is NOT an origin component — which is exactly why it
    /// needs its own clause.</b> An <c>ApiPath</c> of <c>http://x@indexer.example:9117/api</c> resolves
    /// to the same scheme, host and port as its base URL, so the three comparisons below all pass and
    /// the row would be admitted; the composed URI nonetheless carries credentials this deployment
    /// never configured, and they are sent on every request and logged in the URI's authority (neither
    /// the framework's query-string redaction nor <c>LogMessageCleanser</c> touches that part — see
    /// CLAUDE.md §1). The adapter's own guard refuses it, so without this clause the row would pass
    /// here, reach the constructor and throw out of <c>ResolveAsync</c> — failing the WHOLE scope
    /// rather than skipping one row, which is the opposite of what this guard exists to do.</para>
    /// </remarks>
    private static bool IsOnOrigin(Uri baseUrl, string apiPath)
    {
        if (!Uri.TryCreate(baseUrl, apiPath.TrimStart('/'), out var endpoint))
        {
            return false;
        }

        return string.Equals(endpoint.Scheme, baseUrl.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(endpoint.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase)
            && endpoint.Port == baseUrl.Port
            && string.IsNullOrEmpty(endpoint.UserInfo);
    }

    /// <summary>
    /// The row's <c>TimeoutSeconds</c> override as a <see cref="TimeSpan"/>, or <see langword="null"/>
    /// to take the adapter's own default. <c>null</c> means "the default", never "no timeout" — see
    /// <see cref="Source.TimeoutSeconds"/>. A non-positive stored value is treated the same way,
    /// because a zero or negative <see cref="HttpClient.Timeout"/> is rejected by
    /// <see cref="HttpClient"/> itself and would fault the whole set at construction.
    /// </summary>
    private static TimeSpan? RequestTimeoutFor(Source row) =>
        row.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(row.TimeoutSeconds.Value) : null;

    /// <summary>
    /// A FRESH <see cref="HttpClient"/> for one source. See the type remarks: the factory returns a
    /// distinct instance per call, and each adapter assigns its own timeout to the client it owns, so
    /// reusing one across sources would make one row's timeout govern all of them.
    /// </summary>
    private HttpClient CreateClient(string name) => _httpClientFactory.CreateClient(name);
}
