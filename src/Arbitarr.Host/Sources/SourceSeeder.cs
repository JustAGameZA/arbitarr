using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Host.Sources;

/// <summary>
/// #53 stage 53b — the source-resolution read path, implementing the plan's §3.2 OWNER RULING
/// (2026-09-07): <b>seed once from the environment, then the database is authoritative</b>.
///
/// <para><b>Why this design, and not env-as-fallback.</b> The plan's original §3.2 recommended "DB
/// rows win when present; env vars are the fallback". A real incident on 2026-09-07 showed that to
/// be wrong: the live container was recreated on a new image without its environment variables
/// carried across, and the deployment degraded <i>silently</i> — the configured source vanished,
/// <c>nzbHydraConfigured</c> flipped true to false, and nothing logged an error. It could not log
/// an error, because under env-as-permanent-input "the operator removed this source" and "the
/// environment went missing" are indistinguishable states. Under seed-once-then-DB the same
/// container recreation is a non-event: the rows exist, the rows win, and the absent environment is
/// simply irrelevant.</para>
///
/// <para><b>The rules, exactly.</b>
/// <list type="bullet">
/// <item>Seeding happens only when the <c>Sources</c> table is <i>completely empty</i>. Emptiness —
/// not "no row of this kind", not "no row with this name" — is the trigger, which is what makes
/// re-seeding and overwriting structurally impossible rather than merely avoided: after a seed the
/// table is non-empty forever (a row is disabled, never deleted, per the entity's contract), so
/// every subsequent start takes the resolve-only path below.</item>
/// <item>Once any row exists, environment variables are <b>inert</b> — not consulted, not merged,
/// not a fallback. The DB value is used even when an environment variable says otherwise.</item>
/// <item>A seed is logged once, naming each seeded source and stating that env vars are now inert,
/// so the one-way migration is observable in the log rather than invisible.</item>
/// <item>Divergence is warned about: if a row exists and an environment variable for the same
/// source carries a <i>different</i> value, the operator is told, by name, that the DB value is in
/// force. This is the mitigation for this design's one real cost — that editing a compose file
/// after seeding does nothing.</item>
/// </list></para>
///
/// <para><b>Rollback stays safe</b> because seeding never destroys the environment variables: they
/// remain in the container definition, ignored. Rolling back to a pre-53b image reads them again.</para>
///
/// <para><b>Secrets.</b> The API key is read from and written to the write-only Settings row
/// <see cref="SourceRepository.ApiKeySettingName"/>, and is never logged, never echoed, and never
/// included in any message this type produces — not in the seeding line, not in the divergence
/// warning, not in an error path (AC2, and the same posture #43 established).</para>
/// </summary>
public static class SourceSeeder
{
    /// <summary>The <see cref="Source.Kind"/> value for an NZBHydra2 source row.</summary>
    public const string NzbHydraKind = "NzbHydra";

    /// <summary>
    /// Seeds the sources table from <paramref name="environmentConfiguration"/> when it is empty,
    /// then resolves the NZBHydra2 configuration in force and publishes it to
    /// <paramref name="resolved"/>. Runs once at startup, after migrations and before serving.
    /// </summary>
    public static async Task SeedAndResolveAsync(
        ArbitarrDbContext dbContext,
        ResolvedSourceConfiguration resolved,
        EnvironmentSourceConfiguration environmentConfiguration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(environmentConfiguration);
        ArgumentNullException.ThrowIfNull(logger);

        // Emptiness of the whole table is the seed trigger. See the type doc: this is what makes a
        // second seed impossible, because a seeded table is never empty again.
        var tableIsEmpty = !await dbContext.Sources.AnyAsync(cancellationToken);

        if (tableIsEmpty)
        {
            await SeedFromEnvironmentAsync(dbContext, environmentConfiguration, logger, cancellationToken);
        }

        await ResolveFromDatabaseAsync(dbContext, resolved, environmentConfiguration, tableIsEmpty, logger, cancellationToken);
    }

    private static async Task SeedFromEnvironmentAsync(
        ArbitarrDbContext dbContext,
        EnvironmentSourceConfiguration environmentConfiguration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!environmentConfiguration.AnySettingSupplied)
        {
            // No source rows and no environment configuration: a genuinely unconfigured install.
            // Seeding a row from compiled-in defaults would manufacture a source the operator never
            // asked for and would then be authoritative forever, so we deliberately seed nothing.
            logger.LogInformation(
                "Sources: no source rows and no NZBHydra2 environment configuration; nothing to seed. " +
                "Add a source in the database to configure one.");
            return;
        }

        // arb-c29: this is the SECOND write path, not an exemption from the validation the admin
        // endpoints already enforce via SourceRepository.AddAsync/UpdateAsync -- this method writes
        // the row directly against the DbContext instead of through that repository, so it must call
        // SourceRepository.ValidateBaseUrl itself or the rule only closes one of the two doors.
        // Reusing it (rather than a second, possibly-divergent check) is why the .invalid rejection
        // added there also protects this path. A rejected value seeds NOTHING, exactly like the
        // no-configuration-supplied branch above -- there is no compiled-in default base URL for a
        // source (unlike Ollama), so "seed nothing, let the operator configure it" is the only safe
        // fallback; seeding docker-compose.yml's own placeholder unrejected is the arb-c29 incident.
        try
        {
            Arbitarr.Data.Sources.SourceRepository.ValidateBaseUrl(environmentConfiguration.BaseUrl);
        }
        catch (Arbitarr.Data.Sources.SourceValidationException)
        {
            logger.LogWarning(
                "Sources: the NZBHydra2 environment base URL is not a usable address and was NOT " +
                "seeded; no source was created. Configure a source in the database instead. The " +
                "rejected value is deliberately not shown here.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var seeded = new Source
        {
            Kind = NzbHydraKind,
            DisplayName = environmentConfiguration.SourceName,
            BaseUrl = environmentConfiguration.BaseUrl,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Sources.Add(seeded);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(environmentConfiguration.ApiKey))
        {
            // Write-only Settings row, exactly as SourceRepository does (§3.1). The value is never
            // read back into a log or a response.
            dbContext.Settings.Add(new SettingEntry
            {
                Name = SourceRepository.ApiKeySettingName(seeded.Id),
                Value = environmentConfiguration.ApiKey,
                UpdatedAt = now,
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // The one-time seeding line required by §3.2's ruling: it names the seeded source and states
        // plainly that environment variables no longer have any effect. Note the absence of the API
        // key — only whether one was seeded.
        logger.LogInformation(
            "Sources: seeded 1 source from environment configuration on first run: '{DisplayName}' " +
            "(kind {Kind}, {BaseUrl}, API key {ApiKeyState}). The database is now authoritative for " +
            "source configuration; NZBHydra2 environment variables are inert from this point on and " +
            "changing them will have no effect.",
            seeded.DisplayName,
            seeded.Kind,
            seeded.BaseUrl,
            string.IsNullOrWhiteSpace(environmentConfiguration.ApiKey) ? "not set" : "set");
    }

    private static async Task ResolveFromDatabaseAsync(
        ArbitarrDbContext dbContext,
        ResolvedSourceConfiguration resolved,
        EnvironmentSourceConfiguration environmentConfiguration,
        bool justSeeded,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Resolution reads only the database. The environment is not consulted here at all — that is
        // the whole point of the ruling, and it is what makes a container recreated with no
        // environment variables a non-event.
        var source = await dbContext.Sources
            .AsNoTracking()
            .Where(s => s.Kind == NzbHydraKind && s.Enabled)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (source is null)
        {
            resolved.Apply(baseUrl: null, apiKey: null, sourceName: null);
            logger.LogInformation("Sources: no enabled NZBHydra2 source is configured in the database.");
            return;
        }

        var apiKey = await dbContext.Settings
            .AsNoTracking()
            .Where(e => e.Name == SourceRepository.ApiKeySettingName(source.Id))
            .Select(e => e.Value)
            .FirstOrDefaultAsync(cancellationToken);

        resolved.Apply(source.BaseUrl, apiKey, source.DisplayName);

        // AC5: name, per source, where its configuration came from.
        logger.LogInformation(
            "Sources: NZBHydra2 source '{DisplayName}' resolved from the database ({BaseUrl}, API key {ApiKeyState}).",
            source.DisplayName,
            source.BaseUrl,
            string.IsNullOrWhiteSpace(apiKey) ? "not set" : "set");

        if (!justSeeded)
        {
            WarnOnDivergence(source, apiKey, environmentConfiguration, logger);
        }
    }

    /// <summary>
    /// §3.2's divergence mitigation. Once seeded, a changed environment variable does nothing —
    /// a genuine surprise for an operator who edits a compose file and expects an effect. So when a
    /// row exists and the environment still carries a <i>different</i> value for the same source,
    /// say so by name, in the one place the operator will look.
    ///
    /// Only settings the operator actually supplied are compared: a compiled-in default that happens
    /// to differ from a DB row is not divergence, and warning about it would train operators to
    /// ignore this line. The API key is compared for presence and difference only — its value is
    /// never logged.
    /// </summary>
    private static void WarnOnDivergence(
        Source source,
        string? storedApiKey,
        EnvironmentSourceConfiguration environmentConfiguration,
        ILogger logger)
    {
        var divergentFields = new List<string>();

        if (environmentConfiguration.BaseUrlWasSupplied
            && !string.Equals(environmentConfiguration.BaseUrl, source.BaseUrl, StringComparison.Ordinal))
        {
            divergentFields.Add("base URL");
        }

        if (environmentConfiguration.SourceNameWasSupplied
            && !string.Equals(environmentConfiguration.SourceName, source.DisplayName, StringComparison.Ordinal))
        {
            divergentFields.Add("source name");
        }

        if (!string.IsNullOrWhiteSpace(environmentConfiguration.ApiKey)
            && !string.Equals(environmentConfiguration.ApiKey, storedApiKey, StringComparison.Ordinal))
        {
            divergentFields.Add("API key");
        }

        if (divergentFields.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "Sources: NZBHydra2 source '{DisplayName}' has environment configuration that differs from " +
            "the stored configuration ({DivergentFields}). The database value is in force and the " +
            "environment value is ignored; change this source in the database, not in the environment.",
            source.DisplayName,
            string.Join(", ", divergentFields));
    }
}
