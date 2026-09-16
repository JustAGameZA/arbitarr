using Arbitarr.Core.Sources;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Data.Sources;

/// <summary>
/// The per-indexer tuning knobs added in arb-x7w8.1, carried as one optional argument so the many
/// existing call sites that have no opinion about them keep their meaning: omitting this is
/// "whatever the entity defaults say", not "reset everything to zero".
///
/// <para>Every member is nullable, and a <c>null</c> member means LEAVE UNSET / UNCHANGED — which is
/// why <see cref="QueryLimit"/> and <see cref="GrabLimit"/> are <c>int??</c>-shaped by way of the
/// <see cref="SetQueryLimit"/>/<see cref="SetGrabLimit"/> flags rather than a bare <c>int?</c>. For
/// those two columns <c>null</c> is itself a meaningful stored value (unlimited, and NOT the same as
/// <c>0</c> — see <see cref="Entities.Source.QueryLimit"/>), so "leave alone" and "store null" cannot
/// share one representation without collapsing the very distinction the column exists to keep.</para>
/// </summary>
public sealed record SourceOptions
{
    /// <summary>New <see cref="Entities.Source.ApiPath"/>, or null to leave it at its current value.</summary>
    public string? ApiPath { get; init; }

    /// <summary>
    /// New <see cref="Entities.Source.Priority"/>, or null to leave it unchanged.
    ///
    /// <para>This one may safely use the null-check convention, unlike the two limits, because
    /// <see cref="Entities.Source.Priority"/> is a non-nullable <c>int</c> whose every value —
    /// including <c>0</c> — is an ordinary weight with no sentinel meaning. "Unset" is therefore not
    /// a state the column can hold, so <c>null</c> here is free to mean "leave alone" without
    /// colliding with anything storable. The limits cannot do this precisely because <c>null</c> IS
    /// storable there and means unlimited.</para>
    /// </summary>
    public int? Priority { get; init; }

    /// <summary>New <see cref="Entities.Source.TimeoutSeconds"/>. Only applied when <see cref="SetTimeoutSeconds"/> is true.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Whether <see cref="TimeoutSeconds"/> should be written, including when it is null.</summary>
    public bool SetTimeoutSeconds { get; init; }

    /// <summary>New <see cref="Entities.Source.QueryLimit"/>. Only applied when <see cref="SetQueryLimit"/> is true.</summary>
    public int? QueryLimit { get; init; }

    /// <summary>Whether <see cref="QueryLimit"/> should be written, including when it is null (unlimited).</summary>
    public bool SetQueryLimit { get; init; }

    /// <summary>New <see cref="Entities.Source.GrabLimit"/>. Only applied when <see cref="SetGrabLimit"/> is true.</summary>
    public int? GrabLimit { get; init; }

    /// <summary>Whether <see cref="GrabLimit"/> should be written, including when it is null (unlimited).</summary>
    public bool SetGrabLimit { get; init; }

    /// <summary>New <see cref="Entities.Source.LimitsUnit"/>, or null to leave it at its current value.</summary>
    public string? LimitsUnit { get; init; }

    /// <summary>New <see cref="Entities.Source.NzbAccessMode"/>, or null to leave it at its current value.</summary>
    public string? NzbAccessMode { get; init; }
}

/// <summary>
/// #53 stage 53a: persistence for configured upstream sources, with the same
/// validate-at-the-repository-boundary posture as <see cref="Settings.SettingsRepository"/> (AC24 —
/// reject malformed input, never clamp or coerce it into something valid).
///
/// Nothing in this repository is read by the search or dashboard paths yet — that is 53b's job
/// (plan §3.2: DB rows win when present, env vars are the fallback). Stage 53a only proves that
/// rows can be written safely: a malformed base URL or a duplicate display name is rejected before
/// anything touches the database.
///
/// API keys are never a property of <see cref="Source"/> (see that type's doc comment). They are
/// written write-only into the existing <see cref="SettingEntry"/> table under
/// <see cref="ApiKeySettingName"/>, matching §3.1's chosen design (reuse the admin-key precedent
/// rather than build a second secret store). <see cref="HasApiKeyAsync"/> reports presence, so a
/// caller can render "set / not set" (53d) without the value — and it is the only key read any
/// path that projects to the wire is permitted to use.
///
/// 53c adds exactly one reader of the value itself, <see cref="ReadApiKeyForUpstreamRequestAsync"/>,
/// originally for the §3.3 connectivity test, which cannot verify a key without sending it upstream.
/// Since arb-x7w8.3 its one caller is <see cref="SourceCredentialProvider"/>, which hands a
/// <see cref="SourceCredential"/> to the probe and to every later consumer. See that method's
/// comment for the boundary: the value leaves this type only in an outbound request to the
/// configured source, never toward the caller and never into a log or error string.
/// </summary>
public sealed class SourceRepository
{
    private readonly ArbitarrDbContext _dbContext;
    private readonly ILogger<SourceRepository>? _logger;

    /// <summary>
    /// <paramref name="logger"/> is optional so the many existing constructions in tests that have
    /// no opinion about logging keep compiling; the container supplies a real one, which is what
    /// makes <see cref="GetAllAsync"/>'s credential warning reach <c>/api/admin/logs</c>.
    /// </summary>
    public SourceRepository(ArbitarrDbContext dbContext, ILogger<SourceRepository>? logger = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger;
    }

    /// <summary>The write-only Settings row name holding a source's API key. Internal naming detail, not a contract.</summary>
    public static string ApiKeySettingName(long sourceId) => $"source:{sourceId}:api_key";

    /// <summary>
    /// The <see cref="Entities.Source.Kind"/> value for an NZBHydra2 source row.
    ///
    /// <para>arb-pn5: lives here, in <c>Arbitarr.Data</c>, rather than in
    /// <c>Arbitarr.Host.Sources.SourceSeeder</c> (its previous home) because <see cref="ValidateKind"/>
    /// needs it too and <c>Arbitarr.Api</c> must not reference <c>Arbitarr.Host</c> — ADR 0001's
    /// composition-root rule runs the other way (<c>Host</c> depends on <c>Api</c>, never the
    /// reverse). <c>Arbitarr.Data</c> is the one project both <c>Api</c> and <c>Host</c> already
    /// reference, so this is the shared home that adds no new dependency edge. <c>SourceSeeder</c>
    /// now reads this constant rather than defining its own.</para>
    /// </summary>
    public const string NzbHydraKind = "NzbHydra";

    /// <summary>
    /// The <see cref="Entities.Source.Kind"/> value for a direct Newznab (Usenet) indexer — one
    /// Arbitarr queries itself rather than through NZBHydra2 (arb-x7w8.1). Declared here alongside
    /// <see cref="NzbHydraKind"/> for the same ADR 0001 reason: <c>Arbitarr.Data</c> is the one
    /// project both <c>Api</c> and <c>Host</c> already reference.
    /// </summary>
    public const string NewznabKind = "Newznab";

    /// <summary>
    /// The <see cref="Entities.Source.Kind"/> value for a direct Torznab (torrent) indexer, including
    /// a Prowlarr or Jackett endpoint speaking Torznab (arb-x7w8.1). Distinct from
    /// <see cref="NewznabKind"/> rather than a flag on it because the two differ in what they return
    /// (<c>ProtocolKind</c>) and in how a download is served, not merely in a query parameter.
    /// </summary>
    public const string TorznabKind = "Torznab";

    /// <summary>
    /// Every <see cref="Entities.Source.Kind"/> value the running system can actually resolve into a
    /// search source. <see cref="ValidateKind"/> rejects anything outside this set so a casing
    /// mismatch (<c>"nzbhydra"</c>, <c>"NZBHYDRA"</c>) is caught at write time with a 400 instead of
    /// being stored, listed, and silently never matched by <c>SourceSeeder</c>'s ordinal comparison
    /// (arb-pn5) or any future resolver that follows the same pattern.
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownKinds = new[] { NzbHydraKind, NewznabKind, TorznabKind };

    /// <summary>
    /// The accepted <see cref="Entities.Source.LimitsUnit"/> values — the rolling window query and
    /// grab limits are counted over. Matched exactly and ordinally by <see cref="ValidateLimitsUnit"/>,
    /// the same posture as <see cref="KnownKinds"/>.
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownLimitsUnits = new[] { "Hour", "Day" };

    /// <summary>
    /// The <see cref="Entities.Source.NzbAccessMode"/> value for serving a download through Arbitarr,
    /// so the indexer key never leaves the server. The default, and currently the only accepted value.
    /// </summary>
    public const string ProxyAccessMode = "Proxy";

    /// <summary>
    /// The <see cref="Entities.Source.NzbAccessMode"/> value for answering with a <c>Location</c>
    /// header pointing at the upstream URL — which carries the indexer key to the client.
    ///
    /// <para>Named here, but deliberately ABSENT from <see cref="KnownNzbAccessModes"/>, so a write
    /// of it is a 400 by construction. See that field for why.</para>
    /// </summary>
    public const string RedirectAccessMode = "Redirect";

    /// <summary>
    /// The accepted <see cref="Entities.Source.NzbAccessMode"/> values. Matched exactly and ordinally
    /// by <see cref="ValidateNzbAccessMode"/>.
    ///
    /// <para><b><see cref="RedirectAccessMode"/> is deliberately omitted.</b> The owner's ruling is
    /// that Redirect ships OFF and per-indexer opt-in, and that the Settings UI must warn the key is
    /// exposed to the client in that mode. Until that warning exists there is nothing to opt in
    /// through — so this makes the ruling a MECHANISM rather than a note somebody has to remember:
    /// with the value outside this set, storing it is rejected with a 400 at the repository boundary
    /// and no code path can produce a source that exposes its key. Same posture
    /// <see cref="ValidateBaseUrl"/> takes toward the reserved <c>.invalid</c> placeholder — refuse
    /// the value that would quietly do the wrong thing rather than storing it and relying on every
    /// later reader to notice.</para>
    ///
    /// <para><b>arb-x7w8.14 is the bead that adds <see cref="RedirectAccessMode"/> to this array</b>,
    /// in the same change as the Settings UI exposure warning and the positive-control test proving
    /// the <c>Location</c> header (and the key inside it) never reaches logs or events. Adding it
    /// here on its own, ahead of that, re-opens exactly the hole this omission closes.</para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownNzbAccessModes = new[] { ProxyAccessMode };

    /// <summary>
    /// Validates and inserts a new source. Rejects a non-absolute/non-http(s) <paramref name="baseUrl"/>
    /// and a <paramref name="displayName"/> that collides (ordinal, case-insensitive) with an existing
    /// source — both are AC24 rejections, not clamps. If <paramref name="apiKey"/> is supplied it is
    /// written write-only to the Settings table in the same transaction as the source row, so a
    /// failure never leaves a source with a half-written secret.
    /// </summary>
    public async Task<Source> AddAsync(
        string kind,
        string displayName,
        string baseUrl,
        string? apiKey,
        bool enabled,
        CancellationToken cancellationToken,
        SourceOptions? options = null)
    {
        ValidateKind(kind);
        ValidateDisplayName(displayName);
        ValidateBaseUrl(baseUrl);
        await EnsureDisplayNameIsUniqueAsync(displayName, excludingId: null, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var source = new Source
        {
            Kind = kind,
            DisplayName = displayName,
            BaseUrl = baseUrl,
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        ApplyOptions(source, options);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        _dbContext.Sources.Add(source);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(apiKey))
        {
            await UpsertApiKeySettingAsync(source.Id, apiKey, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return source;
    }

    /// <summary>
    /// Validates and updates an existing source's non-secret fields. Rejects the same malformed
    /// input as <see cref="AddAsync"/>. Passing a non-null <paramref name="apiKey"/> replaces the
    /// stored secret; passing <c>null</c> leaves whatever is currently stored untouched (the write-only
    /// contract means there is no way to "read and reapply" the existing value).
    /// </summary>
    public async Task<Source> UpdateAsync(
        long id,
        string kind,
        string displayName,
        string baseUrl,
        string? apiKey,
        bool enabled,
        CancellationToken cancellationToken,
        SourceOptions? options = null)
    {
        var source = await _dbContext.Sources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new SourceValidationException($"Source {id} does not exist.");

        ValidateKind(kind);
        ValidateDisplayName(displayName);
        ValidateBaseUrl(baseUrl);
        await EnsureDisplayNameIsUniqueAsync(displayName, excludingId: id, cancellationToken);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        source.Kind = kind;
        source.DisplayName = displayName;
        source.BaseUrl = baseUrl;
        source.Enabled = enabled;
        ApplyOptions(source, options);
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(apiKey))
        {
            await UpsertApiKeySettingAsync(source.Id, apiKey, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return source;
    }

    /// <summary>
    /// All configured sources, in creation order. Never includes secret material.
    ///
    /// <para><b>Rows written before the arb-4vzm userinfo rejection are not re-validated</b>, here
    /// or anywhere. <see cref="ValidateBaseUrl"/> guards the two WRITE paths; a row stored yesterday
    /// with <c>https://user:pw@…</c> stays exactly as it is, keeps being projected onto
    /// <c>GET /api/admin/sources</c>, and keeps riding on that source's outbound requests. This
    /// method warns about each such row so the condition is visible rather than silent — read-time
    /// rather than at startup because this is the read every admin surface already goes through, so
    /// it needs no new hosted service and no composition-root change.</para>
    ///
    /// <para><b>The response is NOT filtered.</b> Stripping the userinfo in the projection would be
    /// a second, quieter policy disagreeing with the validator — precisely the two-answers defect
    /// this change exists to close — and it would hide from the operator the one thing they need to
    /// act on. A credential the operator can see and remove beats one they cannot.</para>
    /// </summary>
    public async Task<List<Source>> GetAllAsync(CancellationToken cancellationToken)
    {
        var sources = await _dbContext.Sources.AsNoTracking().OrderBy(s => s.Id).ToListAsync(cancellationToken);

        foreach (var source in sources)
        {
            WarnIfBaseUrlCarriesCredentials(source);
        }

        return sources;
    }

    /// <summary>
    /// Warns that a stored base URL carries credentials, naming the ROW ID and nothing else.
    ///
    /// <para><b>Never the value, never the host, never the username.</b> This line lands in the
    /// persistent SQLite log store served at <c>/api/admin/logs</c> (CLAUDE.md §1), and
    /// <c>LogMessageCleanser</c> scrubs QUERY STRINGS — a credential in the userinfo component of a
    /// URL would survive it verbatim. Logging the value in order to complain about it would perform
    /// exactly the leak being complained about. Same constraint as
    /// <c>security-properties-x7w8.md</c>'s P7 (the unknown-Kind warning names the row id and
    /// nothing else, explicitly because the BaseUrl "may carry userinfo until the H1 bead lands" —
    /// this is that bead).</para>
    /// </summary>
    private void WarnIfBaseUrlCarriesCredentials(Source source)
    {
        if (_logger is null)
        {
            return;
        }

        if (!Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
        {
            return;
        }

        _logger.LogWarning(
            "Source {SourceId}'s base URL contains credentials; remove them and store the key in " +
            "the source's API key field. The value is deliberately not shown here.",
            source.Id);
    }

    /// <summary>
    /// Whether a source has an API key stored, without ever exposing the value — the read every
    /// projection to the wire uses, per §3.1/AC2. <c>GET /api/admin/sources</c> renders its
    /// <c>hasApiKey</c> indicator from this and from nothing else.
    /// </summary>
    public Task<bool> HasApiKeyAsync(long sourceId, CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking()
            .AnyAsync(e => e.Name == ApiKeySettingName(sourceId), cancellationToken);

    /// <summary>
    /// Reads a source's stored API key so it can be SENT UPSTREAM, and for no other purpose.
    ///
    /// <para>WHY THIS EXISTS AT ALL, given that <see cref="HasApiKeyAsync"/> was deliberately built
    /// to be the only secret-adjacent read (its doc comment above said exactly that until 53c).
    /// The §3.3 connectivity test has to be a <i>real request against the configured source</i> —
    /// a URL-shape check that passes with a wrong key is precisely the outcome the plan rejects,
    /// because it teaches operators to distrust the button. Authenticating that request requires
    /// the key. There is no way to test an API key without using it.</para>
    ///
    /// <para>THE RULE THIS DOES NOT RELAX. The value returned here goes into an outbound
    /// <c>Authorization</c>/query parameter to the upstream and nowhere else. It is never returned
    /// to a caller, never logged, and never interpolated into an error message or a probe outcome —
    /// <see cref="Arbitarr.Core.Sources.SourceProbeOutcome"/> is a closed enum precisely so that no
    /// probe failure path can carry attacker- or operator-visible free text derived from it. Any
    /// future caller of this method that is not "send it upstream" is a bug.</para>
    ///
    /// <para>THE SINGLE CALLER IS <see cref="SourceCredentialProvider"/>, and since arb-x7w8.3 it
    /// is not the connectivity probe. The probe called this directly while it was the only consumer;
    /// once the search path needs a per-source key too (the startup-resolved
    /// <c>ResolvedSourceConfiguration</c> does not survive N runtime-added indexers), wiring each
    /// consumer to this method would make two callers — and the guarantee IS the call-site count.
    /// Every consumer now takes a <see cref="SourceCredential"/> from that provider instead. A new
    /// consumer costs a new consumer, never a new caller; adding a second call here is the defect
    /// <c>SecretReaderSingleCallerTests</c> exists to catch. See
    /// docs/adr/0018-one-credential-provider-per-secret-family.md.</para>
    /// </summary>
    public Task<string?> ReadApiKeyForUpstreamRequestAsync(long sourceId, CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking()
            .Where(e => e.Name == ApiKeySettingName(sourceId))
            .Select(e => e.Value)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>One source by id, or null when no such row exists. Never carries secret material.</summary>
    public Task<Source?> GetAsync(long id, CancellationToken cancellationToken) =>
        _dbContext.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    /// <summary>
    /// Deletes a source and its write-only API key row in one transaction, so removing a source
    /// never leaves an orphaned secret behind in the Settings table under a now-unused id — dead
    /// weight in a backup (§3.1's flagged consequence for #56) and a value that could be silently
    /// re-adopted if the same surrogate id were ever reissued.
    ///
    /// Returns false for an unknown id so the endpoint can answer 404 rather than report a delete
    /// that did not happen. Unlike <see cref="UpdateAsync"/>, an unknown id is not thrown as a
    /// validation failure here: deleting something already absent is a caller mistake to report,
    /// not malformed input to reject.
    /// </summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var source = await _dbContext.Sources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return false;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        _dbContext.Sources.Remove(source);

        var apiKeyRow = await _dbContext.Settings.FindAsync(new object[] { ApiKeySettingName(id) }, cancellationToken);
        if (apiKeyRow is not null)
        {
            _dbContext.Settings.Remove(apiKeyRow);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Validates and applies the arb-x7w8.1 per-indexer columns onto <paramref name="source"/>.
    /// Validation runs before any assignment so a rejected value never half-writes an entity, the
    /// same AC24 reject-never-clamp posture the rest of this type holds.
    ///
    /// <para>The limit columns are written through their explicit <c>Set…</c> flags rather than by
    /// null-checking the value, because for those two <c>null</c> is a real stored state (unlimited)
    /// that is NOT <c>0</c>. A null-check would make "store unlimited" indistinguishable from "leave
    /// whatever is there", so an operator clearing a limit would silently keep the old one.</para>
    /// </summary>
    private static void ApplyOptions(Source source, SourceOptions? options)
    {
        if (options is null)
        {
            return;
        }

        if (options.ApiPath is not null)
        {
            ValidateApiPath(options.ApiPath);
        }

        if (options.LimitsUnit is not null)
        {
            ValidateLimitsUnit(options.LimitsUnit);
        }

        if (options.NzbAccessMode is not null)
        {
            ValidateNzbAccessMode(options.NzbAccessMode);
        }

        if (options.ApiPath is not null)
        {
            source.ApiPath = options.ApiPath;
        }

        if (options.Priority is not null)
        {
            source.Priority = options.Priority.Value;
        }

        if (options.SetTimeoutSeconds)
        {
            source.TimeoutSeconds = options.TimeoutSeconds;
        }

        if (options.SetQueryLimit)
        {
            source.QueryLimit = options.QueryLimit;
        }

        if (options.SetGrabLimit)
        {
            source.GrabLimit = options.GrabLimit;
        }

        if (options.LimitsUnit is not null)
        {
            source.LimitsUnit = options.LimitsUnit;
        }

        if (options.NzbAccessMode is not null)
        {
            source.NzbAccessMode = options.NzbAccessMode;
        }
    }

    private async Task UpsertApiKeySettingAsync(long sourceId, string apiKey, CancellationToken cancellationToken)
    {
        var name = ApiKeySettingName(sourceId);
        var existing = await _dbContext.Settings.FindAsync(new object[] { name }, cancellationToken);
        if (existing is null)
        {
            _dbContext.Settings.Add(new SettingEntry
            {
                Name = name,
                Value = apiKey,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Value = apiKey;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureDisplayNameIsUniqueAsync(string displayName, long? excludingId, CancellationToken cancellationToken)
    {
        var collides = await _dbContext.Sources.AsNoTracking()
            .Where(s => excludingId == null || s.Id != excludingId)
            .AnyAsync(s => s.DisplayName.ToLower() == displayName.ToLower(), cancellationToken);

        if (collides)
        {
            throw new SourceValidationException($"A source named '{displayName}' already exists.");
        }
    }

    /// <summary>
    /// arb-pn5: rejects any <paramref name="kind"/> that is not exactly one of <see cref="KnownKinds"/>.
    ///
    /// <para>Matched by exact, ordinal name against the known set — the same CLAUDE.md §3 posture as
    /// enum parsing from the wire: no case-insensitive accept-and-normalise, because that would still
    /// let a caller believe <c>"nzbhydra"</c> and <c>"NzbHydra"</c> are the same input rather than
    /// telling them the one spelling that is actually resolved. Before this method existed, a source
    /// created with <c>kind: "nzbhydra"</c> was stored and listed successfully, but
    /// <c>SourceSeeder</c>'s <c>s.Kind == NzbHydraKind</c> ordinal comparison never matched it, so it
    /// was silently never resolved into the search pipeline — no error anywhere.</para>
    /// </summary>
    private static void ValidateKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new SourceValidationException("Source kind must not be empty.");
        }

        if (!KnownKinds.Contains(kind, StringComparer.Ordinal))
        {
            throw new SourceValidationException(
                $"'{kind}' is not a known source kind. Accepted value(s): {string.Join(", ", KnownKinds)}.");
        }
    }

    /// <summary>
    /// Rejects any <paramref name="limitsUnit"/> that is not exactly one of
    /// <see cref="KnownLimitsUnits"/> — same exact-ordinal posture as <see cref="ValidateKind"/> and
    /// for the same reason (CLAUDE.md §3: a closed string set arriving from the wire is matched by
    /// name, never leniently). A stored <c>"day"</c> would be accepted here and then never matched by
    /// the ordinal comparison that decides which rolling window to count over, silently leaving the
    /// source's limits unenforced.
    /// </summary>
    private static void ValidateLimitsUnit(string limitsUnit)
    {
        if (string.IsNullOrWhiteSpace(limitsUnit))
        {
            throw new SourceValidationException("Source limits unit must not be empty.");
        }

        if (!KnownLimitsUnits.Contains(limitsUnit, StringComparer.Ordinal))
        {
            throw new SourceValidationException(
                $"'{limitsUnit}' is not a known limits unit. Accepted value(s): {string.Join(", ", KnownLimitsUnits)}.");
        }
    }

    /// <summary>
    /// Rejects any <paramref name="nzbAccessMode"/> that is not exactly one of
    /// <see cref="KnownNzbAccessModes"/>. Exact ordinal matching matters more here than anywhere
    /// else in this type: this value selects whether the indexer key is exposed to the client, so a
    /// leniently-parsed variant that failed to match <c>"Redirect"</c> — or worse, matched it by
    /// accident — changes a security posture rather than a preference.
    /// </summary>
    private static void ValidateNzbAccessMode(string nzbAccessMode)
    {
        if (string.IsNullOrWhiteSpace(nzbAccessMode))
        {
            throw new SourceValidationException("Source NZB access mode must not be empty.");
        }

        if (!KnownNzbAccessModes.Contains(nzbAccessMode, StringComparer.Ordinal))
        {
            throw new SourceValidationException(
                $"'{nzbAccessMode}' is not a known NZB access mode. Accepted value(s): {string.Join(", ", KnownNzbAccessModes)}.");
        }
    }

    /// <summary>
    /// Rejects an empty <paramref name="apiPath"/>, and one carrying a query string or an embedded
    /// <c>apikey=</c>. Appending the key and the search parameters is the adapter's job (arb-x7w8.2),
    /// which reads the key from its write-only Settings row; a key pasted into this column would be
    /// a second, readable home for the same secret — exactly the property
    /// <see cref="Entities.Source"/>'s doc comment exists to prevent — and it would ride along into
    /// every backup and every response that projects a source.
    ///
    /// <para>Deliberately minimal: this is not URL-path validation. It rejects the two shapes that
    /// break a documented invariant and accepts everything else, because guessing at what a reverse
    /// proxy may legitimately serve is how a correct deployment gets rejected.</para>
    /// </summary>
    private static void ValidateApiPath(string apiPath)
    {
        if (string.IsNullOrWhiteSpace(apiPath))
        {
            throw new SourceValidationException("Source API path must not be empty.");
        }

        if (apiPath.Contains('?', StringComparison.Ordinal))
        {
            throw new SourceValidationException(
                "Source API path must not contain a query string; search parameters are appended by Arbitarr.");
        }

        if (apiPath.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceValidationException(
                "Source API path must not embed an API key; store the key in the source's API key field instead.");
        }
    }

    private static void ValidateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new SourceValidationException("Source display name must not be empty.");
        }
    }

    /// <summary>
    /// Rejects anything that isn't a well-formed absolute http(s) URL, AND a URL whose host is the
    /// RFC 2606 reserved <c>.invalid</c> domain (or the bare host <c>invalid</c>) — a name documented
    /// to never resolve, so seeding or storing one degrades a source to an unreachable upstream that
    /// nothing will ever fix without an operator noticing and correcting it by hand (arb-c29:
    /// docker-compose.yml's own placeholder is exactly such a value, and the seed-once ruling means an
    /// unrejected seed of it is authoritative forever). Public because both write paths that can
    /// create or change a source row must hit this same rule: this type's own <see cref="AddAsync"/>
    /// and <see cref="UpdateAsync"/>, and <c>Arbitarr.Host.Sources.SourceSeeder</c>, which
    /// writes a row directly against the DbContext rather than through this repository.
    ///
    /// <para><b>The first arms are <see cref="UpstreamOrigin.DescribeBaseUrlFault"/>'s, not this
    /// type's</b>, so that what counts as a legitimate origin here is the same answer
    /// <see cref="Arbitarr.Core.Sources.TorznabFeedParser.TryValidateOriginPinnedLink"/> gives for a
    /// link. They had drifted apart in three directions (arb-4vzm userinfo, arb-07ei scheme,
    /// arb-iub9 trailing dot) and every one of those gaps was a defect. That component owns the
    /// parse/scheme floor, the userinfo rejection and the trailing-dot rejection; the
    /// <c>.invalid</c> arm stays here because its reasoning is about seeding a placeholder, not
    /// about what an origin is.</para>
    ///
    /// <para><b>Arm ORDER is load-bearing.</b> The userinfo rejection (inside the component) runs
    /// before the <c>.invalid</c> arm below, which ECHOES <paramref name="baseUrl"/> into its
    /// message. A credential-bearing <c>.invalid</c> URL reaching that arm first would print the
    /// credential into the response body. Do not re-sort these into "simplest check first".</para>
    /// </summary>
    public static void ValidateBaseUrl(string baseUrl)
    {
        var fault = UpstreamOrigin.DescribeBaseUrlFault(baseUrl, out var parsed);
        if (fault is not null)
        {
            throw new SourceValidationException(fault);
        }

        var uri = parsed!;

        if (uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "invalid", StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceValidationException(
                $"'{baseUrl}' uses the reserved .invalid domain, which can never resolve. It is a " +
                "documentation/test address; enter the address your source actually serves on.");
        }
    }
}
