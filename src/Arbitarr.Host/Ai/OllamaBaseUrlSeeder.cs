using Arbitarr.Core.Settings;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Host.Ai;

/// <summary>
/// #89 — the Ollama base URL's resolution path, applying the SAME OWNER RULING #53 settled for
/// sources (2026-09-07): <b>seed once from the environment, then the database is authoritative</b>.
/// <see cref="Arbitarr.Host.Sources.SourceSeeder"/> carries the full reasoning; this type is
/// deliberately its sibling rather than a variation on it, because an operator who has learned how
/// source configuration behaves must not have to learn a second, subtly different rule for the AI
/// backend.
///
/// <para><b>The rules, exactly.</b>
/// <list type="bullet">
/// <item>Seeding happens only when NO <see cref="SettingKey.OllamaBaseUrl"/> row exists. The row's
/// absence — not its value — is the trigger, which is what makes re-seeding structurally impossible:
/// after a seed the row exists forever (the write path replaces it, never deletes it), so every
/// subsequent start takes the resolve-only path.</item>
/// <item>Once the row exists, <c>Arbitarr:Ai:Ollama:BaseUrl</c> is <b>inert</b> — not consulted, not
/// merged, not a fallback. The stored value is used even when the environment says otherwise.</item>
/// <item>A seed is logged once, stating that the environment is now inert, so the one-way migration
/// is observable in the log rather than invisible.</item>
/// <item>Divergence is warned about: if the row exists and the environment carries a DIFFERENT
/// value, the operator is told that the stored value is in force. This is the mitigation for this
/// design's one real cost — that editing a compose file after seeding does nothing.</item>
/// </list></para>
///
/// <para><b>Why the seed is written even when the operator set nothing.</b> Unlike a source — where
/// seeding nothing is correct, because manufacturing a source the operator never asked for would
/// then be authoritative forever — the AI backend has a meaningful compiled-in default
/// (<see cref="OllamaBaseUrlResolver.DefaultBaseUrl"/>), which is the address the code used before
/// #89 anyway. Writing it makes the value VISIBLE and EDITABLE on the settings surface from the
/// first start, rather than leaving an empty field that reads as "not configured" while
/// classification quietly works against a default the operator cannot see.</para>
///
/// <para><b>THE SEED IS VALIDATED, and this is the second write path — not an exemption from the
/// first.</b> <see cref="SettingsValidator.ValidateOllamaBaseUrl"/> runs here on the environment
/// value before it is stored, exactly as it does on <c>PUT /api/admin/ai/ollama</c>. An earlier
/// revision checked only for whitespace and named the validator in this comment without calling it,
/// which left <c>Arbitarr:Ai:Ollama:BaseUrl</c> as an unvalidated door into the same row: on a
/// fresh database a value like <c>ftp://x</c> or a non-URL seeded a row that made Program.cs's
/// <c>new Uri(...)</c> throw on every classification, and a value carrying userinfo
/// (<c>http://user:pass@host</c>) seeded a credential that would then be served back on
/// <c>GET /api/admin/ai/ollama</c> and written into the persistent log store by
/// <c>IHttpClientFactory</c>'s logging handler. An invariant enforced on only one of two writers is
/// not an invariant. A rejected value falls back to
/// <see cref="OllamaBaseUrlResolver.DefaultBaseUrl"/> rather than aborting startup, so a
/// misconfigured environment degrades to a working default the operator can then correct on the
/// Settings page.</para>
///
/// <para><b>No secret is in play — BECAUSE of that validation, not instead of it.</b> The base URL
/// is not a credential (Ollama has no authentication), so unlike
/// <see cref="Arbitarr.Host.Sources.SourceSeeder"/> this type logs the value itself, which is what
/// makes the divergence warning actionable. That is only safe because every path that can write the
/// row rejects userinfo. The one exception is the REJECTION warning below, which deliberately does
/// NOT echo the value it rejected: the whole reason a value reaches that branch may be that it
/// carries credentials, and <see cref="Exception.Message"/> interpolates the
/// offending value, so neither the value nor the exception message may be logged there.</para>
/// </summary>
public static class OllamaBaseUrlSeeder
{
    /// <summary>
    /// Seeds the <see cref="SettingKey.OllamaBaseUrl"/> row from <paramref name="environmentBaseUrl"/>
    /// when no row exists, then warns if a surviving environment value diverges from the stored one.
    /// Runs once at startup, after migrations and before serving.
    /// </summary>
    /// <param name="environmentBaseUrl">
    /// The configured <c>Arbitarr:Ai:Ollama:BaseUrl</c>, or null when the operator supplied none.
    /// Null means "seed the compiled-in default", and means there is nothing to diverge from later.
    /// </param>
    public static async Task SeedAsync(
        ArbitarrDbContext dbContext,
        string? environmentBaseUrl,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(logger);

        var name = SettingKey.OllamaBaseUrl.ToString();
        var existing = await dbContext.Settings
            .AsNoTracking()
            .Where(e => e.Name == name)
            .Select(e => e.Value)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            var seeded = ValidatedOrDefault(environmentBaseUrl, logger);

            dbContext.Settings.Add(new SettingEntry
            {
                Name = name,
                Value = seeded,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "AI: seeded the Ollama base URL from {Origin} on first run ({BaseUrl}). The database " +
                "is now authoritative for this address; the Arbitarr:Ai:Ollama:BaseUrl environment " +
                "variable is inert from this point on and changing it will have no effect. Change it " +
                "on the Settings page instead.",
                // Compares against what was actually stored rather than re-testing the environment
                // for blankness: a REJECTED environment value also lands on the default, and
                // reporting that as "from environment configuration" would tell the operator their
                // setting was honoured when ValidatedOrDefault has just warned that it was not.
                string.Equals(seeded, environmentBaseUrl, StringComparison.Ordinal)
                    ? "environment configuration"
                    : "the built-in default",
                seeded);
            return;
        }

        // Resolution itself reads only the database (OllamaBaseUrlResolver). The environment is
        // consulted here for exactly one purpose: telling the operator it is being ignored.
        if (!string.IsNullOrWhiteSpace(environmentBaseUrl)
            && !string.Equals(environmentBaseUrl, existing, StringComparison.Ordinal))
        {
            // Only the STORED value is echoed. The environment value is described but never
            // printed: it is unvalidated here (this branch does not seed, so nothing rejects it),
            // so it may carry the credentials the seed path refuses to store, and the whole point
            // of that refusal is that such a value never reaches the log store.
            logger.LogWarning(
                "AI: the Arbitarr:Ai:Ollama:BaseUrl environment variable is set and differs from the " +
                "stored Ollama base URL ({StoredBaseUrl}). The stored value is in force and the " +
                "environment value is ignored; change this address on the Settings page, not in the " +
                "environment.",
                existing);
        }
    }

    /// <summary>
    /// Returns <paramref name="environmentBaseUrl"/> when it passes
    /// <see cref="SettingsValidator.ValidateOllamaBaseUrl"/>, and
    /// <see cref="OllamaBaseUrlResolver.DefaultBaseUrl"/> when it is absent or rejected.
    ///
    /// <para><b>The rejection warning names the setting but NEVER the value, and that asymmetry is
    /// the point.</b> A value can land here precisely because it embeds credentials, and
    /// <see cref="Exception.Message"/> quotes the value it rejected — so logging
    /// either the value or <c>ex.Message</c> would write the credential into the persistent log
    /// store served at <c>/api/admin/logs</c>, which is the exact leak the validator exists to
    /// prevent. The operator has the value in their own compose file; they need to be told it was
    /// rejected and why the class of value is wrong, not shown it back.</para>
    ///
    /// <para>Startup is not aborted on a bad value: falling back to a working default keeps the
    /// instance serving (and correctable through the UI), where throwing would turn one mistyped
    /// environment variable into a container that will not boot.</para>
    /// </summary>
    private static string ValidatedOrDefault(string? environmentBaseUrl, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(environmentBaseUrl))
        {
            return OllamaBaseUrlResolver.DefaultBaseUrl;
        }

        try
        {
            SettingsValidator.ValidateOllamaBaseUrl(environmentBaseUrl);
            return environmentBaseUrl;
        }
        catch (SettingsValidationException)
        {
            logger.LogWarning(
                "AI: the Arbitarr:Ai:Ollama:BaseUrl environment variable is not a usable Ollama " +
                "address and was NOT stored; the built-in default ({BaseUrl}) was seeded instead. It " +
                "must be an absolute http or https URL, and must not embed credentials " +
                "(user:password@host) — Ollama has no authentication, and credentials in the address " +
                "would be written to the request log. Set a valid address on the Settings page. The " +
                "rejected value is deliberately not shown here because it may contain a credential.",
                OllamaBaseUrlResolver.DefaultBaseUrl);

            return OllamaBaseUrlResolver.DefaultBaseUrl;
        }
    }
}
