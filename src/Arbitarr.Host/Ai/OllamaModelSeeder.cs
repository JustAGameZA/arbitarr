using Arbitarr.Core.Settings;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Host.Ai;

/// <summary>
/// #112 — the Ollama MODEL's resolution path, applying the SAME OWNER RULING #53 settled for sources
/// and #89 applied to the base URL: <b>seed once from the environment, then the database is
/// authoritative</b>. <see cref="OllamaBaseUrlSeeder"/> carries the full reasoning; this type is
/// deliberately its sibling rather than a variation on it, because an operator who has learned how
/// the base URL behaves must not have to learn a second, subtly different rule for the model that
/// sits beside it in the same settings section.
///
/// <para><b>The rules, exactly.</b>
/// <list type="bullet">
/// <item>Seeding happens only when NO <see cref="SettingKey.OllamaModel"/> row exists. The row's
/// absence — not its value — is the trigger, which is what makes re-seeding structurally impossible:
/// after a seed the row exists forever (the write path replaces it, never deletes it), so every
/// subsequent start takes the resolve-only path.</item>
/// <item>Once the row exists, <c>Arbitarr:Ai:Ollama:Model</c> is <b>inert</b> — not consulted, not
/// merged, not a fallback. The stored value is used even when the environment says otherwise.</item>
/// <item>A seed is logged once, stating that the environment is now inert.</item>
/// <item>Divergence is warned about: if the row exists and the environment carries a DIFFERENT
/// value, the operator is told that the stored value is in force.</item>
/// </list></para>
///
/// <para><b>Why the seed is written even when the operator set nothing.</b> The same reason the base
/// URL's is: there is a meaningful compiled-in default
/// (<see cref="OllamaModelResolver.DefaultModel"/>), which is the model the code used before #112
/// anyway. Writing it makes the value VISIBLE on the settings surface from the first start, rather
/// than leaving the AI section showing nothing while classification quietly runs against a model the
/// operator cannot see — which is precisely the invisibility #112 exists to end.</para>
///
/// <para><b>THE SEED IS VALIDATED, and this is the second write path — not an exemption from the
/// first.</b> <see cref="SettingsValidator.ValidateOllamaModel"/> runs here on the environment value
/// before it is stored, exactly as it does on <c>PUT /api/admin/ai/ollama</c>. An invariant enforced
/// on only one of two writers is not an invariant: without this, a model name carrying a trailing
/// newline from a compose file would seed a row that failed every <c>/api/chat</c> call while the
/// UI showed a value that looked right. A rejected value falls back to the compiled-in default
/// rather than aborting startup, so a misconfigured environment degrades to a working instance the
/// operator can then correct on the Settings page.</para>
///
/// <para><b>The value IS logged, unlike a source key and like the base URL.</b> A model name is not
/// a credential — it is served back on <c>GET /api/admin/ai/ollama</c> and rendered in a picker — so
/// echoing it is what makes the divergence warning actionable. The one exception is the REJECTION
/// warning, which deliberately does not echo: a rejected value is by definition malformed (it
/// carries whitespace or control characters, or is absurdly long), and pasting that into a log line
/// is how a control character reaches the log store.</para>
/// </summary>
public static class OllamaModelSeeder
{
    /// <summary>
    /// Seeds the <see cref="SettingKey.OllamaModel"/> row from <paramref name="environmentModel"/>
    /// when no row exists, then warns if a surviving environment value diverges from the stored one.
    /// Runs once at startup, after migrations and before serving.
    /// </summary>
    /// <param name="environmentModel">
    /// The configured <c>Arbitarr:Ai:Ollama:Model</c>, or null when the operator supplied none.
    /// Null means "seed the compiled-in default", and means there is nothing to diverge from later.
    /// </param>
    public static async Task SeedAsync(
        ArbitarrDbContext dbContext,
        string? environmentModel,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(logger);

        var name = SettingKey.OllamaModel.ToString();
        var existing = await dbContext.Settings
            .AsNoTracking()
            .Where(e => e.Name == name)
            .Select(e => e.Value)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            var seeded = ValidatedOrDefault(environmentModel, logger);

            dbContext.Settings.Add(new SettingEntry
            {
                Name = name,
                Value = seeded,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "AI: seeded the Ollama model from {Origin} on first run ({Model}). The database is " +
                "now authoritative for this value; the Arbitarr:Ai:Ollama:Model environment variable " +
                "is inert from this point on and changing it will have no effect. Choose a model on " +
                "the Settings page instead — the connection test lists what your instance actually has.",
                // Compares against what was actually stored rather than re-testing the environment
                // for blankness: a REJECTED environment value also lands on the default, and
                // reporting that as "from environment configuration" would tell the operator their
                // setting was honoured when ValidatedOrDefault has just warned that it was not.
                string.Equals(seeded, environmentModel, StringComparison.Ordinal)
                    ? "environment configuration"
                    : "the built-in default",
                seeded);
            return;
        }

        // Resolution itself reads only the database (OllamaModelResolver). The environment is
        // consulted here for exactly one purpose: telling the operator it is being ignored.
        if (!string.IsNullOrWhiteSpace(environmentModel)
            && !string.Equals(environmentModel, existing, StringComparison.Ordinal))
        {
            // Only the STORED value is echoed. The environment value is described but never printed:
            // it is unvalidated here (this branch does not seed, so nothing rejects it), so it may
            // carry the control characters the seed path refuses to store.
            logger.LogWarning(
                "AI: the Arbitarr:Ai:Ollama:Model environment variable is set and differs from the " +
                "stored Ollama model ({StoredModel}). The stored value is in force and the environment " +
                "value is ignored; change the model on the Settings page, not in the environment.",
                existing);
        }
    }

    /// <summary>
    /// Returns <paramref name="environmentModel"/> when it passes
    /// <see cref="SettingsValidator.ValidateOllamaModel"/>, and
    /// <see cref="OllamaModelResolver.DefaultModel"/> when it is absent or rejected.
    ///
    /// <para>The rejection warning names the setting but NEVER the value, and neither does it log
    /// <see cref="Exception.Message"/>, which quotes what it rejected. A value
    /// reaches this branch precisely because it is malformed — whitespace, a control character, or
    /// absurd length — and writing that into the persistent log store served at
    /// <c>/api/admin/logs</c> is how a control character ends up in a page rendering those logs. The
    /// operator has the value in their own compose file; they need to be told it was rejected and
    /// why the class of value is wrong, not shown it back.</para>
    ///
    /// <para>Startup is not aborted on a bad value: falling back to a working default keeps the
    /// instance classifying (and correctable through the UI), where throwing would turn one mistyped
    /// environment variable into a container that will not boot.</para>
    /// </summary>
    private static string ValidatedOrDefault(string? environmentModel, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(environmentModel))
        {
            return OllamaModelResolver.DefaultModel;
        }

        try
        {
            SettingsValidator.ValidateOllamaModel(environmentModel);
            return environmentModel;
        }
        catch (SettingsValidationException)
        {
            logger.LogWarning(
                "AI: the Arbitarr:Ai:Ollama:Model environment variable is not a usable Ollama model " +
                "name and was NOT stored; the built-in default ({Model}) was seeded instead. A model " +
                "tag looks like 'qwen2.5:7b-instruct-q4_K_M': it must be non-blank, must contain no " +
                "whitespace or control characters, and must be at most {MaxLength} characters. Choose " +
                "a model on the Settings page instead. The rejected value is deliberately not shown " +
                "here because a malformed value may carry control characters.",
                OllamaModelResolver.DefaultModel,
                SettingsValidator.OllamaModelMaxLength);

            return OllamaModelResolver.DefaultModel;
        }
    }
}
