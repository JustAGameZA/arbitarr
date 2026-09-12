using Arbitarr.Core.Media;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Media;

/// <summary>
/// Validation failure for an *arr instance's stored configuration. Rejected at the repository
/// boundary rather than clamped, matching the AC24 posture <c>SettingsRepository</c>,
/// <c>SourceRepository</c> and <c>NotificationRepository</c> all take.
/// </summary>
public sealed class ArrInstanceValidationException : Exception
{
    public ArrInstanceValidationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Persistence for the Sonarr instance Arbitarr resolves series identities against
/// (<c>Arbitarr.Media.Providers.ArrApiProvider</c>): its base URL and its write-only API key.
///
/// <para><b>NO NEW TABLE, DELIBERATELY.</b> Both values are colon-namespaced rows in the existing
/// <see cref="SettingEntry"/> table — the convention <c>SourceRepository</c> established for source
/// API keys (<c>source:{id}:api_key</c>) and <c>NotificationRepository</c> followed for the webhook
/// URL. That is not merely reuse: a new table would accumulate rows and would therefore need its
/// own retention wired into <c>MaintenanceJob</c>, and a retention policy with no scheduler is a
/// defect a diff cannot show. These rows are FIXED IN NUMBER — exactly two — so they do not
/// accumulate and there is nothing to prune.</para>
///
/// <para><b>THE API KEY IS A SECRET, HANDLED EXACTLY LIKE A SOURCE API KEY.</b> Its row name is
/// colon-namespaced, so no <see cref="Arbitarr.Core.Settings.SettingKey"/> enum value can produce
/// it and it can never surface through <c>GET /api/admin/settings</c> (which projects from
/// <c>SettingsCatalog.Entries</c>, never from this table). That unreachability is a MECHANISM, not a
/// coincidence — see CLAUDE.md §1. Presence is read as a BOOL by <see cref="HasApiKeyAsync"/>,
/// which is the only read any path that projects to the wire may use.
/// <see cref="ReadApiKeyForUpstreamRequestAsync"/> is the single reader of the value, and — exactly
/// as <c>SourceRepository.ReadApiKeyForUpstreamRequestAsync</c> states for the key it mirrors — the
/// value it returns goes into one outbound request to the configured Sonarr instance and nowhere
/// else. It is never returned to a caller, never logged, and never interpolated into an error
/// message or a probe outcome (<c>SourceProbeOutcome</c> is a closed enum precisely so no probe
/// failure path can carry text derived from it). Any future caller of that method which is not
/// "send it to Sonarr" is a bug.</para>
///
/// <para><b>THE BASE URL IS NOT A SECRET and is served back freely</b>, exactly as
/// <c>OllamaBaseUrl</c> is (see <c>AdminAiEndpoints</c>'s type doc). Sonarr's credential is the
/// key, which travels in its own row; <see cref="ValidateBaseUrl"/> rejects a URL carrying userinfo
/// so that stays true and no credential can be smuggled into the address. Making an operator retype
/// an address they cannot see, to defend a secret that is not in it, would be cargo-culting the
/// write-only idiom rather than applying it.</para>
/// </summary>
public sealed class ArrInstanceRepository
{
    /// <summary>
    /// The row holding the Sonarr base URL. Colon-namespaced for the same unreachability reason as
    /// the key below, even though this value is not a secret: the two belong to one section and
    /// splitting the namespace would put half of it back in reach of the catalog projection.
    /// Internal naming detail, not a contract.
    /// </summary>
    public const string SonarrBaseUrlSettingName = "arr:sonarr:base_url";

    /// <summary>
    /// The write-only row holding the Sonarr API key. Colon-namespaced so it is unreachable from
    /// the settings catalog projection — see the type doc. Internal naming detail, not a contract.
    /// </summary>
    public const string SonarrApiKeySettingName = "arr:sonarr:api_key";

    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly IArrInstanceEpoch _epoch;

    /// <summary>
    /// arb-iiy: <paramref name="epoch"/> is bumped after a successful <see cref="SetAsync"/> so
    /// caches keyed on the resolved instance (<c>SeriesTitleResolver</c>'s tvdbid-to-title memo)
    /// stop serving the previous server's answers. It defaults to a FRESH INSTANCE rather than to a
    /// shared one, so a test that constructs this repository directly and never repoints gets a
    /// private counter instead of process-wide state; production passes the DI singleton, which is
    /// the only instance the resolver also sees.
    /// </summary>
    public ArrInstanceRepository(
        ArbitarrDbContext dbContext,
        TimeProvider? timeProvider = null,
        IArrInstanceEpoch? epoch = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _epoch = epoch ?? new ArrInstanceEpoch();
    }

    /// <summary>
    /// The stored Sonarr base URL, or null when none is configured. Safe to project to the wire —
    /// it is not a credential, and <see cref="ValidateBaseUrl"/> is what keeps it from carrying one.
    /// </summary>
    public Task<string?> GetBaseUrlAsync(CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking()
            .Where(e => e.Name == SonarrBaseUrlSettingName)
            .Select(e => e.Value)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Whether a Sonarr API key is stored, without ever exposing the value — the read every
    /// projection to the wire uses. The admin surface renders its "set / not set" indicator from
    /// this and from nothing else.
    /// </summary>
    public Task<bool> HasApiKeyAsync(CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking()
            .AnyAsync(e => e.Name == SonarrApiKeySettingName, cancellationToken);

    /// <summary>
    /// Reads the stored Sonarr API key so it can be SENT UPSTREAM to that instance, and for no
    /// other purpose. See the type doc for the rule this does not relax: the value goes into an
    /// outbound request to the configured Sonarr and nowhere else — never to a caller, never to a
    /// log, never into an error message or a probe outcome.
    /// </summary>
    public Task<string?> ReadApiKeyForUpstreamRequestAsync(CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking()
            .Where(e => e.Name == SonarrApiKeySettingName)
            .Select(e => e.Value)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Writes the Sonarr configuration. Both values are validated BEFORE either is written, so a
    /// rejected key cannot leave a saved address behind it — reject-never-clamp is also
    /// reject-never-partially-apply (the ruling <c>AdminAiEndpoints</c> states for its own pair).
    ///
    /// <para><paramref name="apiKey"/> follows the source-API-key contract exactly: null LEAVES THE
    /// STORED VALUE ALONE, a non-empty value REPLACES it. There is deliberately no way to express
    /// "give me back what is stored", because the caller never had it — which is also why omitting
    /// the field from an edit must not clear it. <see cref="ClearAsync"/> is the one way to
    /// un-configure a key, kept explicit rather than folded in here, where conflating the two would
    /// make an ordinary edit that omits the field silently destroy the operator's configuration.</para>
    ///
    /// <para><b>arb-iiy: THE EPOCH IS BUMPED AFTER THE WRITE SUCCEEDS, and only then.</b> Caches
    /// keyed on the resolved instance — <c>SeriesTitleResolver</c>'s tvdbid-to-title memo — fold
    /// <see cref="IArrInstanceEpoch.Current"/> into their keys, so repointing Sonarr at a different
    /// server makes the previous server's answers unreachable instead of serving them for the rest
    /// of the memo TTL. Placing the bump after <c>SaveChangesAsync</c> is what keeps a REJECTED
    /// input (a bad URL, a blank key) from discarding a cache that is still correct: nothing was
    /// written, so nothing has gone stale. The bump lives here rather than at the admin endpoint
    /// because this is the single write path for the instance, and a second write site added later
    /// would otherwise have to remember to invalidate.</para>
    ///
    /// <para>This bump fires for ANY accepted write, including one that only rotates the API key and
    /// leaves the base URL unchanged: the memo it evicts stays correct for the instance that answers
    /// next, and a refill costs one more HTTP call, so bumping unconditionally is harmless rather than
    /// merely tolerated.</para>
    /// </summary>
    public async Task SetAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken)
    {
        ValidateBaseUrl(baseUrl);
        if (apiKey is not null)
        {
            ValidateApiKey(apiKey);
        }

        await UpsertAsync(SonarrBaseUrlSettingName, baseUrl, cancellationToken);

        if (!string.IsNullOrEmpty(apiKey))
        {
            await UpsertAsync(SonarrApiKeySettingName, apiKey, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // arb-iiy: after the write, never before — see this method's doc. A rejected input threw
        // above and never reaches here, so a bump is evidence that the stored instance really did
        // change.
        _epoch.Bump();
    }

    /// <summary>
    /// Unconfigures the Sonarr instance entirely: removes BOTH the base URL and the API key rows in
    /// one operation.
    ///
    /// <para><b>THERE IS DELIBERATELY NO KEY-ONLY CLEAR</b> (ADR 0010; bead arb-c26, ratified
    /// 2026-09-08). The shared rule is "secrets are never readable, and omission never clears", and
    /// a secret is cleared only by deleting the thing that OWNS it — deleting a source deletes its
    /// key (<c>SourceRepository.DeleteAsync</c>), and the webhook's bodiless DELETE is that rule's
    /// completion for a singleton with no owning row. This method is the same shape for this
    /// singleton. A key-only clear was considered and rejected: it would leave a half-configured
    /// instance — an address with no credential — which is exactly the state the Sources surface
    /// never offers, and it would pre-empt a decision belonging to the whole secret class rather
    /// than to one new feature.</para>
    ///
    /// <para>Returns false when nothing was stored, so a caller can report truthfully rather than
    /// claim a delete that did not happen.</para>
    /// </summary>
    public async Task<bool> ClearAsync(CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Settings
            .Where(e => e.Name == SonarrBaseUrlSettingName || e.Name == SonarrApiKeySettingName)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return false;
        }

        // Both rows go in ONE SaveChanges, so an unconfigure can never half-apply and strand a key
        // under an address that is gone — the orphaned-secret failure SourceRepository.DeleteAsync
        // guards against for its own pair.
        _dbContext.Settings.RemoveRange(rows);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Rejects anything that is not an absolute http(s) URL, and — load-bearing — anything carrying
    /// USERINFO. The userinfo rejection is what lets <see cref="GetBaseUrlAsync"/> be projected to
    /// the wire and the address be logged: <c>http://user:secret@host</c> would otherwise smuggle a
    /// credential into a value this type documents as non-secret, and
    /// <c>IHttpClientFactory</c>'s logging handler writes the full absolute URI at Information
    /// (CLAUDE.md §1). Mirrors <c>SettingsValidator.ValidateOllamaBaseUrl</c>, whose comment states
    /// the same dependency.
    /// </summary>
    public static void ValidateBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArrInstanceValidationException("A Sonarr base URL is required.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new ArrInstanceValidationException("The Sonarr base URL must be an absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArrInstanceValidationException("The Sonarr base URL must use http or https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArrInstanceValidationException("The Sonarr base URL must not contain credentials.");
        }
    }

    /// <summary>
    /// Rejects a blank key. An empty string reaching here is a submitted blank, not an omission —
    /// <see cref="SetAsync"/> treats null as "leave it alone" and only non-null values arrive here,
    /// so the two cases stay distinguishable.
    /// </summary>
    public static void ValidateApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArrInstanceValidationException("The Sonarr API key must not be blank.");
        }
    }

    private async Task UpsertAsync(string name, string value, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Settings.FindAsync([name], cancellationToken);
        if (existing is null)
        {
            _dbContext.Settings.Add(new SettingEntry
            {
                Name = name,
                Value = value,
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            return;
        }

        existing.Value = value;
        existing.UpdatedAt = _timeProvider.GetUtcNow();
    }
}
