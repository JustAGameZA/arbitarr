using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Sources;

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
/// for the §3.3 connectivity test, which cannot verify a key without sending it upstream. See that
/// method's comment for the boundary: the value leaves this type only in an outbound request to the
/// configured source, never toward the caller and never into a log or error string.
/// </summary>
public sealed class SourceRepository
{
    private readonly ArbitarrDbContext _dbContext;

    public SourceRepository(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <summary>The write-only Settings row name holding a source's API key. Internal naming detail, not a contract.</summary>
    public static string ApiKeySettingName(long sourceId) => $"source:{sourceId}:api_key";

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
        CancellationToken cancellationToken)
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
        CancellationToken cancellationToken)
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
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(apiKey))
        {
            await UpsertApiKeySettingAsync(source.Id, apiKey, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return source;
    }

    /// <summary>All configured sources, in creation order. Never includes secret material.</summary>
    public Task<List<Source>> GetAllAsync(CancellationToken cancellationToken) =>
        _dbContext.Sources.AsNoTracking().OrderBy(s => s.Id).ToListAsync(cancellationToken);

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

    private static void ValidateKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new SourceValidationException("Source kind must not be empty.");
        }
    }

    private static void ValidateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new SourceValidationException("Source display name must not be empty.");
        }
    }

    private static void ValidateBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SourceValidationException($"'{baseUrl}' is not a valid absolute http(s) URL.");
        }
    }
}
