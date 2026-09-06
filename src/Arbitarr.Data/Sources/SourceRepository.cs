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
/// rather than build a second secret store) and are never read back by this repository — only
/// <see cref="HasApiKeyAsync"/> reports presence, so a caller can render "set / not set" (53d)
/// without ever being able to leak the value through this type.
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
    /// Whether a source has an API key stored, without ever exposing the value — the only
    /// secret-adjacent read this repository offers, per §3.1/AC2.
    /// </summary>
    public Task<bool> HasApiKeyAsync(long sourceId, CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking()
            .AnyAsync(e => e.Name == ApiKeySettingName(sourceId), cancellationToken);

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
