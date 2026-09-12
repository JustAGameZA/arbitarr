using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Media;

/// <summary>
/// Persistence for the Radarr instance Arbitarr reads the movie library and download queue from:
/// its base URL and its write-only API key.
///
/// <para><b>WHY THIS TYPE DUPLICATES <see cref="ArrInstanceRepository"/> RATHER THAN GENERALISING
/// IT</b> (arb-arrq D3, settled by the owner 2026-09-12). The rejected alternative was to
/// parameterise <see cref="ArrInstanceRepository"/> over an instance KIND and have both *arr types
/// share one implementation. It was rejected on two grounds, recorded here so a THIRD instance kind
/// reopens the question deliberately rather than by accident. First, two instances do not pay for
/// the indirection: the shared type would be reached only ever through two call sites, each of
/// which would then have to carry the kind. Second, and load-bearing, the Sonarr type's doc comments
/// encode SONARR-SPECIFIC reasoning — the <c>IArrInstanceEpoch</c> bump and the identity resolver it
/// exists for — which a generic type would have to either lose or carry conditionally, and a comment
/// that is true of one caller and false of the other is worse than no comment. If a third *arr is
/// added, the arithmetic changes and this decision should be revisited.</para>
///
/// <para><b>THIS TYPE DOES NOT BUMP <c>IArrInstanceEpoch</c>, DELIBERATELY.</b> That epoch is
/// SONARR'S: it exists to evict <c>SeriesTitleResolver</c>'s tvdbid-to-title memo when the Sonarr
/// instance is repointed. Radarr has no memo keyed on it, and Radarr is deliberately OUT of identity
/// resolution (<c>ArrApiProvider</c> stays Sonarr-only, arb-arrq D4), so bumping it on a Radarr
/// write would evict a Sonarr cache that is still entirely correct for an unrelated write. The
/// absence of a bump here is a decision, not an omission. If Radarr ever gains a cache of its own, it
/// needs its OWN epoch rather than a share of this one.</para>
///
/// <para><b>NO NEW TABLE, DELIBERATELY.</b> Both values are colon-namespaced rows in the existing
/// <see cref="SettingEntry"/> table — the convention <c>SourceRepository</c> established for source
/// API keys (<c>source:{id}:api_key</c>) and which <see cref="ArrInstanceRepository"/> follows for
/// Sonarr's pair. That is not merely reuse: a new table would accumulate rows and would therefore
/// need its own retention wired into <c>MaintenanceJob</c>, and a retention policy with no scheduler
/// is a defect a diff cannot show. These rows are FIXED IN NUMBER — exactly two — so they do not
/// accumulate and there is nothing to prune.</para>
///
/// <para><b>THE API KEY IS A SECRET, HANDLED EXACTLY LIKE A SOURCE API KEY.</b> Its row name is
/// colon-namespaced, so no <see cref="Arbitarr.Core.Settings.SettingKey"/> enum value can produce it
/// and it can never surface through <c>GET /api/admin/settings</c> (which projects from
/// <c>SettingsCatalog.Entries</c>, never from this table). That unreachability is a MECHANISM, not a
/// coincidence — see CLAUDE.md §1, and NEVER add either row to <c>SettingsCatalog</c>. Presence is
/// read as a BOOL by <see cref="HasApiKeyAsync"/>, which is the only read any path that projects to
/// the wire may use. <see cref="ReadApiKeyForUpstreamRequestAsync"/> is the single reader of the
/// value, and the value it returns goes into one outbound request to the configured Radarr instance
/// and nowhere else. It is never returned to a caller, never logged, and never interpolated into an
/// error message or a probe outcome (<c>SourceProbeOutcome</c> is a closed enum precisely so no probe
/// failure path can carry text derived from it). Any future caller of that method which is not "send
/// it to Radarr" is a bug.</para>
///
/// <para><b>THE BASE URL IS NOT A SECRET and is served back freely</b>, exactly as Sonarr's is.
/// Radarr's credential is the key, which travels in its own row; <see cref="ValidateBaseUrl"/>
/// rejects a URL carrying userinfo so that stays true and no credential can be smuggled into the
/// address. Making an operator retype an address they cannot see, to defend a secret that is not in
/// it, would be cargo-culting the write-only idiom rather than applying it.</para>
/// </summary>
public sealed class RadarrInstanceRepository
{
    /// <summary>
    /// The row holding the Radarr base URL. Colon-namespaced for the same unreachability reason as
    /// the key below, even though this value is not a secret: the two belong to one section and
    /// splitting the namespace would put half of it back in reach of the catalog projection.
    /// Internal naming detail, not a contract.
    /// </summary>
    public const string RadarrBaseUrlSettingName = "arr:radarr:base_url";

    /// <summary>
    /// The write-only row holding the Radarr API key. Colon-namespaced so it is unreachable from
    /// the settings catalog projection — see the type doc. Internal naming detail, not a contract.
    /// </summary>
    public const string RadarrApiKeySettingName = "arr:radarr:api_key";

    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public RadarrInstanceRepository(ArbitarrDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The stored Radarr base URL, or null when none is configured. Safe to project to the wire —
    /// it is not a credential, and <see cref="ValidateBaseUrl"/> is what keeps it from carrying one.
    /// </summary>
    public Task<string?> GetBaseUrlAsync(CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking()
            .Where(e => e.Name == RadarrBaseUrlSettingName)
            .Select(e => e.Value)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Whether a Radarr API key is stored, without ever exposing the value — the read every
    /// projection to the wire uses. The admin surface renders its "set / not set" indicator from
    /// this and from nothing else.
    /// </summary>
    public Task<bool> HasApiKeyAsync(CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking()
            .AnyAsync(e => e.Name == RadarrApiKeySettingName, cancellationToken);

    /// <summary>
    /// Reads the stored Radarr API key so it can be SENT UPSTREAM to that instance, and for no other
    /// purpose.
    ///
    /// <para><b>THIS METHOD MUST HAVE EXACTLY ONE CALLER</b> —
    /// <see cref="RadarrCredentialProvider"/> — which is the same guarantee
    /// <c>ArrInstanceRepository.ReadApiKeyForUpstreamRequestAsync</c> makes for Sonarr's key
    /// (CLAUDE.md §1; docs/standards/architecture.md's secrets-mechanisms list). The guarantee IS the
    /// call-site count: a second caller is a second place to audit, and no comment saying "do not add
    /// a third" recovers what the second one cost. See the type doc for the rule this does not relax:
    /// the value goes into an outbound request to the configured Radarr and nowhere else — never to a
    /// caller, never to a log, never into an error message or a probe outcome.</para>
    /// </summary>
    public Task<string?> ReadApiKeyForUpstreamRequestAsync(CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking()
            .Where(e => e.Name == RadarrApiKeySettingName)
            .Select(e => e.Value)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Writes the Radarr configuration. Both values are validated BEFORE either is written, so a
    /// rejected key cannot leave a saved address behind it — reject-never-clamp is also
    /// reject-never-partially-apply.
    ///
    /// <para><paramref name="apiKey"/> follows the source-API-key contract exactly: null LEAVES THE
    /// STORED VALUE ALONE, a non-empty value REPLACES it. There is deliberately no way to express
    /// "give me back what is stored", because the caller never had it — which is also why omitting
    /// the field from an edit must not clear it. <see cref="ClearAsync"/> is the one way to
    /// un-configure a key, kept explicit rather than folded in here, where conflating the two would
    /// make an ordinary edit that omits the field silently destroy the operator's configuration.</para>
    ///
    /// <para><b>NO EPOCH BUMP HERE</b> — see the type doc. <c>IArrInstanceEpoch</c> is Sonarr's, and
    /// bumping it for a Radarr write would evict a Sonarr memo that is still correct.</para>
    /// </summary>
    public async Task SetAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken)
    {
        ValidateBaseUrl(baseUrl);
        if (apiKey is not null)
        {
            ValidateApiKey(apiKey);
        }

        await UpsertAsync(RadarrBaseUrlSettingName, baseUrl, cancellationToken);

        if (!string.IsNullOrEmpty(apiKey))
        {
            await UpsertAsync(RadarrApiKeySettingName, apiKey, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Unconfigures the Radarr instance entirely: removes BOTH the base URL and the API key rows in
    /// one operation.
    ///
    /// <para><b>THERE IS DELIBERATELY NO KEY-ONLY CLEAR</b> (ADR 0010; bead arb-c26, ratified
    /// 2026-09-08). The shared rule is "secrets are never readable, and omission never clears", and a
    /// secret is cleared only by deleting the thing that OWNS it — deleting a source deletes its key
    /// (<c>SourceRepository.DeleteAsync</c>), and the webhook's bodiless DELETE is that rule's
    /// completion for a singleton with no owning row. This method is the same shape for this
    /// singleton. A key-only clear would leave a half-configured instance — an address with no
    /// credential — which is exactly the state the Sources surface never offers, and it would pre-empt
    /// a decision belonging to the whole secret class rather than to one new feature.</para>
    ///
    /// <para>Returns false when nothing was stored, so a caller can report truthfully rather than
    /// claim a delete that did not happen.</para>
    /// </summary>
    public async Task<bool> ClearAsync(CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Settings
            .Where(e => e.Name == RadarrBaseUrlSettingName || e.Name == RadarrApiKeySettingName)
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
    /// credential into a value this type documents as non-secret, and <c>IHttpClientFactory</c>'s
    /// logging handler writes every path segment of the absolute URI at Information (CLAUDE.md §1 —
    /// only the QUERY STRING is collapsed). Mirrors
    /// <c>ArrInstanceRepository.ValidateBaseUrl</c>, whose comment states the same dependency.
    /// </summary>
    public static void ValidateBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArrInstanceValidationException("A Radarr base URL is required.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new ArrInstanceValidationException("The Radarr base URL must be an absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArrInstanceValidationException("The Radarr base URL must use http or https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArrInstanceValidationException("The Radarr base URL must not contain credentials.");
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
            throw new ArrInstanceValidationException("The Radarr API key must not be blank.");
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
