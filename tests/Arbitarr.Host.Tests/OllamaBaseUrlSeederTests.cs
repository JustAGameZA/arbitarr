using Arbitarr.Core.Settings;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Settings;
using Arbitarr.Host.Ai;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// #89: <see cref="OllamaBaseUrlSeeder"/>, which applies #53's OWNER RULING — seed once from the
/// environment, then the database is authoritative — to the Ollama base URL.
///
/// <para><b>The property this file exists to pin: SEED ONCE.</b> A seeder that re-seeds on every
/// start looks identical on a fresh install and silently reverts the operator's setting on the next
/// restart, which is the exact failure the ruling was written to prevent. So the central assertion
/// is that a SECOND run against a changed row leaves that row alone
/// (<see cref="Does_not_re_seed_or_overwrite_a_row_the_operator_changed"/>), not merely that the
/// first run wrote something.</para>
///
/// <para>The other half of the ruling is the divergence warning: once seeded, editing the compose
/// file does nothing, which is a genuine surprise, so the operator is told by name that the stored
/// value is in force. That line is the mitigation for this design's one real cost and is therefore
/// behaviour worth pinning rather than incidental output.</para>
///
/// <para>All addresses are RFC 5737 documentation forms; no real address is committed.</para>
/// </summary>
public sealed class OllamaBaseUrlSeederTests : IDisposable
{
    private const string EnvironmentBaseUrl = "http://192.0.2.30:11434";
    private const string OperatorBaseUrl = "http://192.0.2.40:11434";

    private readonly SqliteTestDatabase _database = new("arbitarr-89-seeder");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext() => CreateContext(_database.ConnectionString);

    /// <summary>
    /// A context on a NAMED database file, so a test needing two independent databases can have
    /// them. The positive controls below rely on this: a control that seeded into the shared file
    /// would leave its row in place, the run under test would then take the divergence branch
    /// instead of the seed branch, and the "secret must not appear" assertion would be testing a
    /// different code path than the one it names.
    /// </summary>
    private static ArbitarrDbContext CreateContext(string connectionString)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(connectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    /// <summary>
    /// Runs <paramref name="plant"/> against a throwaway database and asserts the planted needle
    /// DOES reach the seeder's log — the positive control CLAUDE.md §4 requires before an absence
    /// assertion means anything. Without it, "the secret does not appear" passes just as happily
    /// when the secret was never in play or the search string was wrong.
    /// </summary>
    private static async Task AssertSecretWouldBeDetectedAsync(
        string needle,
        Func<ArbitarrDbContext, RecordingLogger, Task> plant)
    {
        // Its own database, disposed here rather than by the class fixture: the control must not
        // share a file with the run under test (see the CreateContext remarks above).
        using var controlDatabase = new SqliteTestDatabase("arbitarr-89-control");

        using (var control = CreateContext(controlDatabase.ConnectionString))
        {
            var controlLogger = new RecordingLogger();
            await plant(control, controlLogger);

            Assert.Contains(
                controlLogger.Entries,
                e => e.Message.Contains(needle, StringComparison.Ordinal));
        }
    }

    private static async Task<string?> ReadStoredAsync(ArbitarrDbContext context) =>
        await context.Settings
            .AsNoTracking()
            .Where(e => e.Name == SettingKey.OllamaBaseUrl.ToString())
            .Select(e => e.Value)
            .FirstOrDefaultAsync();

    [Fact]
    public async Task Seeds_the_environment_value_on_a_first_run()
    {
        using var context = CreateContext();
        var logger = new RecordingLogger();

        await OllamaBaseUrlSeeder.SeedAsync(context, EnvironmentBaseUrl, logger);

        Assert.Equal(EnvironmentBaseUrl, await ReadStoredAsync(context));

        var seedLine = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("seeded the Ollama base URL"));
        Assert.Contains("inert", seedLine.Message);
        Assert.Contains("no effect", seedLine.Message);
    }

    /// <summary>
    /// With no environment value the compiled-in default is written rather than nothing. Unlike a
    /// source — where seeding nothing is correct, because inventing one would be authoritative
    /// forever — the AI backend has a real default that the code used before #89 anyway, and writing
    /// it is what makes the value visible and editable on the settings surface from the first start.
    /// </summary>
    [Fact]
    public async Task Seeds_the_built_in_default_when_the_environment_supplies_nothing()
    {
        using var context = CreateContext();
        var logger = new RecordingLogger();

        await OllamaBaseUrlSeeder.SeedAsync(context, environmentBaseUrl: null, logger);

        Assert.Equal(OllamaBaseUrlResolver.DefaultBaseUrl, await ReadStoredAsync(context));
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("built-in default"));
    }

    /// <summary>
    /// <b>THE SEED-ONCE ASSERTION.</b> A second start must not revert an operator's change, even
    /// when the environment still carries the value the row was originally seeded from. A seeder
    /// that overwrites would pass every "the row exists" assertion while silently undoing the
    /// setting on every restart.
    /// </summary>
    [Fact]
    public async Task Does_not_re_seed_or_overwrite_a_row_the_operator_changed()
    {
        using var context = CreateContext();

        await OllamaBaseUrlSeeder.SeedAsync(context, EnvironmentBaseUrl, new RecordingLogger());

        // The operator then changes it on the Settings page.
        var row = await context.Settings.SingleAsync(e => e.Name == SettingKey.OllamaBaseUrl.ToString());
        row.Value = OperatorBaseUrl;
        await context.SaveChangesAsync();

        // A later restart, with the environment variable still set to its original value.
        await OllamaBaseUrlSeeder.SeedAsync(context, EnvironmentBaseUrl, new RecordingLogger());

        Assert.Equal(OperatorBaseUrl, await ReadStoredAsync(context));
        // And exactly one row: a second seed must not add a duplicate either.
        Assert.Equal(
            1,
            await context.Settings.CountAsync(e => e.Name == SettingKey.OllamaBaseUrl.ToString()));
    }

    /// <summary>
    /// The ruling's divergence mitigation: once seeded, a changed environment variable does nothing,
    /// so the operator is told that the stored value is in force.
    ///
    /// <para>The STORED value appears (it is what is in force, and it is not a secret — every path
    /// that writes the row rejects credentials in it). The ENVIRONMENT value deliberately does not:
    /// this branch does not seed, so nothing validates it, which means it may carry exactly the
    /// userinfo the seed path refuses to store — and this log line lands in the persistent store
    /// served at /api/admin/logs. <see cref="A_divergent_environment_credential_is_not_logged"/>
    /// pins that with a positive control.</para>
    /// </summary>
    [Fact]
    public async Task Warns_when_the_environment_diverges_from_the_stored_value()
    {
        using var context = CreateContext();
        await OllamaBaseUrlSeeder.SeedAsync(context, OperatorBaseUrl, new RecordingLogger());

        var logger = new RecordingLogger();
        await OllamaBaseUrlSeeder.SeedAsync(context, EnvironmentBaseUrl, logger);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(OperatorBaseUrl, warning.Message, StringComparison.Ordinal);
        Assert.Contains("in force", warning.Message);
        // The stored value is untouched by the warning path.
        Assert.Equal(OperatorBaseUrl, await ReadStoredAsync(context));
    }

    /// <summary>
    /// An environment value EQUAL to the stored one is not divergence, and warning about it would
    /// train operators to ignore the line that matters.
    /// </summary>
    [Fact]
    public async Task Does_not_warn_when_the_environment_agrees_with_the_stored_value()
    {
        using var context = CreateContext();
        await OllamaBaseUrlSeeder.SeedAsync(context, EnvironmentBaseUrl, new RecordingLogger());

        var logger = new RecordingLogger();
        await OllamaBaseUrlSeeder.SeedAsync(context, EnvironmentBaseUrl, logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// A container recreated WITHOUT its environment variables is the incident that produced the
    /// ruling. It must be a non-event: the row wins, and nothing warns about an absent variable.
    /// </summary>
    [Fact]
    public async Task An_absent_environment_variable_is_a_non_event()
    {
        using var context = CreateContext();
        await OllamaBaseUrlSeeder.SeedAsync(context, OperatorBaseUrl, new RecordingLogger());

        var logger = new RecordingLogger();
        await OllamaBaseUrlSeeder.SeedAsync(context, environmentBaseUrl: null, logger);

        Assert.Equal(OperatorBaseUrl, await ReadStoredAsync(context));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// <b>THE SECOND WRITE PATH IS VALIDATED.</b> The environment variable reaches the same row as
    /// PUT /api/admin/ai/ollama, so a value the PUT would reject must not get in through the seeder
    /// either — an invariant enforced on one of two writers is not an invariant.
    ///
    /// <para>Each case is a distinct way the value goes wrong and a distinct consequence: a non-URL
    /// and a non-http(s) scheme make Program.cs's <c>new Uri(...)</c>/the client fail on every
    /// classification, and userinfo seeds a credential into a row that is served back on
    /// GET /api/admin/ai/ollama and logged with every request URI.</para>
    /// </summary>
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://ollama.example.com")]
    [InlineData("http://user:pass@ollama.example.com:11434")]
    public async Task Rejects_an_unusable_environment_value_and_seeds_the_default_instead(string environmentBaseUrl)
    {
        using var context = CreateContext();
        var logger = new RecordingLogger();

        await OllamaBaseUrlSeeder.SeedAsync(context, environmentBaseUrl, logger);

        Assert.Equal(OllamaBaseUrlResolver.DefaultBaseUrl, await ReadStoredAsync(context));

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Arbitarr:Ai:Ollama:BaseUrl", warning.Message, StringComparison.Ordinal);
        Assert.Contains("was NOT stored", warning.Message, StringComparison.Ordinal);

        // The seed line must not claim the environment was honoured when it was rejected.
        var seedLine = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("seeded the Ollama base URL"));
        Assert.Contains("built-in default", seedLine.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejected environment value may be rejected precisely BECAUSE it carries credentials, so
    /// neither the value nor SettingsValidationException.Message (which quotes it) may be logged:
    /// these lines land in the persistent store served at /api/admin/logs.
    ///
    /// <para><b>POSITIVE CONTROL (CLAUDE.md §4).</b> An absence assertion passes just as happily
    /// when the secret was never in play, so this first proves the assertion CAN fail: the password
    /// is planted in a value that IS logged (the seeded-value slot of a valid address) and the same
    /// search is shown to find it there. Only then is the real rejection path asserted clean. The
    /// control also proves the search string and comparison are right, which is what #80's
    /// first-versus-last-key miss came down to.</para>
    /// </summary>
    [Fact]
    public async Task A_rejected_environment_credential_is_not_logged()
    {
        const string Needle = "seeder-canary-a1b2c3";

        // POSITIVE CONTROL: the same needle, in a value the seeder DOES log. Proves this search
        // finds a planted secret in seeder output, so its absence below is evidence and not an
        // empty set. (A host label, not userinfo — a userinfo value would be rejected and never
        // reach the log line, which is the very thing under test.)
        await AssertSecretWouldBeDetectedAsync(
            Needle,
            (control, controlLogger) =>
                OllamaBaseUrlSeeder.SeedAsync(control, $"http://{Needle}.example.com:11434", controlLogger));

        using var context = CreateContext();
        var logger = new RecordingLogger();

        await OllamaBaseUrlSeeder.SeedAsync(context, $"http://admin:{Needle}@ollama.example.com:11434", logger);

        // The credential appears in no log line, and the row holds the default rather than it.
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(Needle, StringComparison.Ordinal));
        Assert.Equal(OllamaBaseUrlResolver.DefaultBaseUrl, await ReadStoredAsync(context));
    }

    /// <summary>
    /// The divergence branch does not seed, so nothing validates the environment value there — and
    /// it must therefore not be echoed either. Same positive control as above.
    /// </summary>
    [Fact]
    public async Task A_divergent_environment_credential_is_not_logged()
    {
        const string Needle = "divergence-canary-d4e5f6";

        await AssertSecretWouldBeDetectedAsync(
            Needle,
            (control, controlLogger) =>
                OllamaBaseUrlSeeder.SeedAsync(control, $"http://{Needle}.example.com:11434", controlLogger));

        using var context = CreateContext();
        await OllamaBaseUrlSeeder.SeedAsync(context, OperatorBaseUrl, new RecordingLogger());

        var logger = new RecordingLogger();
        await OllamaBaseUrlSeeder.SeedAsync(
            context,
            $"http://admin:{Needle}@ollama.example.com:11434",
            logger);

        // This really is the divergence branch: the row is the one the first call seeded, so the
        // second call warned rather than seeded. Asserting it guards against the stale-row mistake
        // that made an earlier draft of this test exercise a different branch than it named.
        Assert.Equal(OperatorBaseUrl, await ReadStoredAsync(context));

        // The divergence warning fired (so the assertion below is not vacuous for want of a line)…
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("in force", StringComparison.Ordinal));
        // …and it carries the stored value, never the environment credential.
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(Needle, StringComparison.Ordinal));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
