using Arbitarr.Core.Ai;
using Arbitarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Settings;

/// <summary>
/// #112: resolves the Ollama MODEL in force, reading the <see cref="SettingKey.OllamaModel"/> row
/// through <see cref="OllamaModelCache"/> so a settings write takes effect on the next use rather
/// than at the next restart. The deliberate sibling of <see cref="OllamaBaseUrlResolver"/>, and
/// every note there applies here.
///
/// <para><b>This is the second half of the "no restart" seam.</b> Before #112 the model was fixed at
/// start-up in the <c>OllamaOptions</c> singleton, so changing it meant recycling the process —
/// which is what let an operator point Arbitarr at an instance that had never pulled the configured
/// model and have every classification fail open with no way to correct it from the UI.
/// <c>OllamaClient</c> now resolves the model PER CALL through here, and <c>AiModelIdentity</c>
/// follows the same value so a model change invalidates the verdict cache exactly as R17 requires
/// (the cache keys on the identity).</para>
///
/// <para><b>Why the read lives here rather than in Core.</b> <see cref="OllamaModelCache"/> holds
/// the value but cannot fetch it — Core references no persistence layer (AC6). This type is the
/// Data-side half, and it goes through the same <c>Settings</c> table every other setting uses
/// rather than a store of its own.</para>
///
/// <para><b>A missing or blank row falls back to <see cref="DefaultModel"/></b> rather than
/// throwing, matching <see cref="OllamaBaseUrlResolver"/>: a bad row must not fault the
/// classification path, and the fallback is the compiled-in default rather than the environment
/// value, because after the first start the environment is inert (see <c>OllamaModelSeeder</c>) and
/// reading it again on a fallback path would quietly reintroduce env-as-fallback for one edge
/// case.</para>
/// </summary>
public sealed class OllamaModelResolver
{
    /// <summary>
    /// The compiled-in default, matching the model Program.cs defaulted to before #112 so an
    /// upgrade changes nothing an operator did not ask for.
    /// </summary>
    public const string DefaultModel = "qwen2.5:7b-instruct-q4_K_M";

    private readonly ArbitarrDbContext _dbContext;
    private readonly OllamaModelCache _cache;

    public OllamaModelResolver(ArbitarrDbContext dbContext, OllamaModelCache cache)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// The model in force. Serves the cached value when it is fresh; otherwise reads the stored
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
                .Where(e => e.Name == SettingKey.OllamaModel.ToString())
                .Select(e => e.Value)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var resolved = IsUsableModel(stored) ? stored! : DefaultModel;
            if (_cache.TrySetIfGenerationMatches(resolved, generation))
            {
                return resolved;
            }

            // A settings write completed while this query was in flight. The cache was invalidated
            // by that write, so retry against the new generation rather than publishing this old row.
        }
    }

    /// <summary>
    /// The model in force, read SYNCHRONOUSLY. Exists for exactly one caller: the
    /// <c>AiModelIdentity</c> DI factory in Program.cs, which builds a record and therefore has no
    /// await to hang the read on.
    ///
    /// <para><b>Why a second method rather than blocking on <see cref="GetAsync"/>.</b>
    /// <c>GetAsync(...).GetAwaiter().GetResult()</c> inside a DI factory is the shape that
    /// deadlocks, and it would also mean the async path's <c>ConfigureAwait(false)</c> is the only
    /// thing standing between this and a hung request. A genuinely synchronous EF read has no such
    /// hazard, and one indexed row from SQLite is the same query either way — the codebase reads
    /// synchronously in several stores already. This is NOT a general-purpose accessor: the
    /// classification path resolves through <see cref="GetAsync"/>, and adding a second async
    /// caller here would be a step back towards blocking on the request path.</para>
    /// </summary>
    public string Get()
    {
        while (true)
        {
            var cached = _cache.GetCurrentIfFresh(out var generation);
            if (cached is not null)
            {
                return cached;
            }

            var stored = _dbContext.Settings
                .AsNoTracking()
                .Where(e => e.Name == SettingKey.OllamaModel.ToString())
                .Select(e => e.Value)
                .FirstOrDefault();

            var resolved = IsUsableModel(stored) ? stored! : DefaultModel;
            if (_cache.TrySetIfGenerationMatches(resolved, generation))
            {
                return resolved;
            }
        }
    }

    /// <summary>
    /// Whether a stored row can actually be sent as Ollama's <c>model</c> field.
    ///
    /// <para><b>Why this is checked on READ when both writers already validate.</b> The same
    /// reasoning <c>OllamaBaseUrlResolver.IsUsableAddress</c> records: a row written before
    /// this validation existed, or edited directly in the database, would otherwise make every
    /// classification fail with no settings write able to clear it. Deliberately NARROWER than
    /// <see cref="SettingsValidator.ValidateOllamaModel"/> — this asks only "can this be sent",
    /// while the length cap and the whitespace rejection are write-time policy, and a legacy row
    /// that breaches them must still resolve to something rather than to nothing.</para>
    /// </summary>
    private static bool IsUsableModel(string? stored) => !string.IsNullOrWhiteSpace(stored);
}
