using Arbitarr.Core.Ai;
using Arbitarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Settings;

/// <summary>
/// #89: resolves the Ollama base URL in force, reading the <see cref="SettingKey.OllamaBaseUrl"/>
/// row through <see cref="OllamaBaseUrlCache"/> so a settings write takes effect on the next use
/// rather than at the next restart.
///
/// <para><b>This is the "no restart" seam.</b> <c>IOllamaClient</c> is registered scoped and
/// resolves its address through here on construction, so the value a classification call uses is
/// the value stored when that call was made. The write path invalidates the cache, and the very
/// next resolution re-reads the row. Nothing about the AI layer is rebuilt and no process is
/// recycled.</para>
///
/// <para><b>Why the read lives here rather than in Core.</b> <see cref="OllamaBaseUrlCache"/> holds
/// the value but cannot fetch it — Core references no persistence layer (AC6). This type is the
/// Data-side half, and it goes through the same <c>Settings</c> table every other setting uses
/// rather than a store of its own.</para>
///
/// <para><b>A missing or unusable row falls back to <c>fallbackBaseUrl</c></b> (the
/// seed default) rather than throwing, which is what <c>SettingsReader</c> does for every other
/// setting and for the same reason: a bad row must not fault the classification path. Note the
/// fallback here is the compiled-in default, NOT the environment value — after the first start the
/// environment is inert (see <c>OllamaBaseUrlSeeder</c>), so reading it again on a fallback path
/// would quietly reintroduce env-as-fallback for one edge case.</para>
/// </summary>
public sealed class OllamaBaseUrlResolver
{
    /// <summary>
    /// The compiled-in default, matching the in-cluster service name Program.cs used before #89.
    /// Never a LAN IP (CONTRIBUTING.md's topology rule) — a service name is what a container
    /// deployment actually resolves.
    /// </summary>
    public const string DefaultBaseUrl = "http://ollama:11434";

    private readonly ArbitarrDbContext _dbContext;
    private readonly OllamaBaseUrlCache _cache;

    public OllamaBaseUrlResolver(ArbitarrDbContext dbContext, OllamaBaseUrlCache cache)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// The base URL in force. Serves the cached value when it is fresh; otherwise reads the stored
    /// row, publishes it, and returns it.
    /// </summary>
    public async Task<string> GetAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var cached = _cache.GetCurrentIfFresh(out var generation);
            if (cached is not null)
            {
                return cached;
            }

            var stored = await _dbContext.Settings
                .AsNoTracking()
                .Where(e => e.Name == SettingKey.OllamaBaseUrl.ToString())
                .Select(e => e.Value)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var resolved = IsUsableAddress(stored) ? stored! : DefaultBaseUrl;
            if (_cache.TrySetIfGenerationMatches(resolved, generation))
            {
                return resolved;
            }

            // A settings write completed while this query was in flight. The cache was invalidated
            // by that write, so retry against the new generation rather than publishing this old row.
        }
    }

    /// <summary>
    /// Whether a stored row can actually be used as a base address — absent, blank, and
    /// unparseable-or-non-http(s) values all fall back to <see cref="DefaultBaseUrl"/>.
    ///
    /// <para><b>Why this is checked on READ when both writers already validate.</b> Every path that
    /// writes the row runs <c>SettingsValidator.ValidateOllamaBaseUrl</c>
    /// (<c>AdminAiEndpoints</c> and <c>OllamaBaseUrlSeeder</c>), so a bad row should be
    /// unreachable — but the consumer of this value is the classification path, which does
    /// <c>new Uri(...)</c> with it. A row written before that validation existed, or edited
    /// directly in the database, would otherwise throw on EVERY classification, converting one bad
    /// row into a total outage that no settings write could clear (the failure happens before the
    /// operator's corrected value is ever read). Falling back keeps the instance classifying
    /// against a sane default and leaves the UI able to fix the row. Deliberately narrower than the
    /// full validator: this only asks "can this be used", while rejecting userinfo is a write-time
    /// policy, and a legacy row carrying one must still resolve to something rather than to
    /// nothing.</para>
    /// </summary>
    private static bool IsUsableAddress(string? stored) =>
        !string.IsNullOrWhiteSpace(stored)
        && Uri.TryCreate(stored, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
