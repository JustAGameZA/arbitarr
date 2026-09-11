using Arbitarr.Data.Logging;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Host.Logging;

/// <summary>
/// Registers the SQLite log sink (#65) together with the category filters that keep framework
/// chatter out of it (arb-1of).
///
/// <para>
/// EXTRACTED FROM <c>Program.cs</c> ON PURPOSE. The registration used to sit inline in the
/// composition root's top-level statements, which are not independently constructible — the two
/// existing guards for that region (<c>ProgramClassifierPollingWorkerTests</c>,
/// <c>ProgramOllamaHttpClientTests</c>) therefore assert against the TEXT of Program.cs rather than
/// its behaviour. A source-level grep can show that a filter line is present; it cannot show that
/// the filter actually drops a line, which is the only thing worth asserting here. Moving the
/// registration behind one static method lets the test build a real <see cref="ILoggerFactory"/>
/// through the same code the host runs, so the assertions are behavioural.
/// </para>
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// Framework categories demoted to Warning in the SQLite sink. These are the two that dominate
    /// the store at Information: ASP.NET Core writes a request-start and a request-end line per
    /// request. On a polling deployment that is the overwhelming majority of rows, and it pushes
    /// the lines an operator actually opened the Logs tab for off the first page.
    ///
    /// <para>
    /// <c>System.Net.Http.HttpClient</c> IS DELIBERATELY NOT IN THIS LIST, though it is the other
    /// obvious source of Information-level noise (a start/end pair per outbound call). Those rows
    /// are the surface <c>UpstreamApiKeyLogRedactionTests</c> and
    /// <c>SonarrKeyIsScrubbedFromLogsTests</c> read to prove an upstream API key is scrubbed from
    /// the URIs the registered clients log. Both assert <c>NotEmpty</c> on those rows FIRST, as
    /// their positive control (CLAUDE.md §4), so demoting the category to Warning empties the
    /// collection and SILENCES THE CONTROL rather than tripping a visible assertion about secrets.
    /// Adding it here was tried under arb-1of and failed in exactly that way. Anything revisiting
    /// this must keep those rows reaching the store, or move those tests onto another evidence
    /// surface first — it is not a free noise win.
    /// </para>
    /// </summary>
    private static readonly string[] NoisyFrameworkCategories =
    [
        "Microsoft",
    ];

    /// <summary>
    /// Adds <see cref="SqliteLoggerProvider"/> and its per-provider category filters.
    /// </summary>
    /// <param name="logging">The host's logging builder.</param>
    /// <param name="logStore">The already-created store the sink writes to.</param>
    /// <param name="onError">
    /// Invoked when the sink itself cannot write. See the call site in <c>Program.cs</c> for why
    /// this must not route back through <see cref="ILogger"/>.
    /// </param>
    public static void AddArbitarrSqliteLogging(
        ILoggingBuilder logging,
        LogStore logStore,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(logStore);

        // Information matches the level Sonarr registers its own database target at.
        logging.AddProvider(new SqliteLoggerProvider(logStore, LogLevel.Information, onError: onError));

        foreach (var category in NoisyFrameworkCategories)
        {
            // AddFilter<T> binds by the CONCRETE PROVIDER TYPE, not by a [ProviderAlias] name — so
            // SqliteLoggerProvider needs no alias attribute for this to apply, and verifying that
            // was the open question in arb-1of. Console is a different provider type and is
            // therefore untouched: docker logs stays the raw, unfiltered view, which #65's comment
            // at the top of Program.cs requires.
            //
            // The categories are PREFIXES: the filter applies to the category and everything under
            // it, so "Microsoft" covers Microsoft.AspNetCore.Hosting.Diagnostics and the rest.
            //
            // Filters see the ORIGINAL category name. SqliteLoggerProvider.CreateLogger strips the
            // "Arbitarr." prefix for storage, but that happens after the filter has already decided,
            // so these prefixes must be written as the framework emits them.
            logging.AddFilter<SqliteLoggerProvider>(category, LogLevel.Warning);
        }
    }
}
