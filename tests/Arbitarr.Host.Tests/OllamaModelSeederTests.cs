using Arbitarr.Core.Settings;
using Arbitarr.Data;
using Arbitarr.Data.Settings;
using Arbitarr.Host.Ai;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// #112: <see cref="OllamaModelSeeder"/>, applying #53's OWNER RULING — seed once from the
/// environment, then the database is authoritative — to the Ollama model, exactly as
/// <see cref="OllamaBaseUrlSeederTests"/> pins it for the base URL.
///
/// <para><b>The property this file exists to pin: SEED ONCE.</b> A seeder that re-seeds on every
/// start looks identical on a fresh install and silently reverts the operator's choice on the next
/// restart. The central assertion is therefore that a SECOND run against a changed row leaves that
/// row alone, not merely that the first run wrote something.</para>
///
/// <para><b>And one #112 owns outright: an EXISTING installation gets a model row.</b> A deployment
/// that already ran #89 has a base URL row and no model row, and it is exactly the installation this
/// issue is for. <see cref="Seeds_a_model_row_on_an_upgrade_that_already_has_a_base_url_row"/> pins
/// that the model seed is not gated on the base URL's absence.</para>
///
/// <para>Model names here are real public tags (not secrets) or <c>placeholder-*</c>.</para>
/// </summary>
public sealed class OllamaModelSeederTests : IDisposable
{
    private const string EnvironmentModel = "llama3.1:8b";
    private const string OperatorModel = "phi4:14b";

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"arbitarr-112-model-seeder-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ArbitarrDbContext CreateContext() => CreateContext(_dbPath);

    private static ArbitarrDbContext CreateContext(string dbPath)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static async Task<string?> ReadStoredAsync(ArbitarrDbContext context) =>
        await context.Settings
            .AsNoTracking()
            .Where(e => e.Name == SettingKey.OllamaModel.ToString())
            .Select(e => e.Value)
            .FirstOrDefaultAsync();

    [Fact]
    public async Task Seeds_the_environment_value_on_a_first_run()
    {
        using var context = CreateContext();
        var logger = new RecordingLogger();

        await OllamaModelSeeder.SeedAsync(context, EnvironmentModel, logger);

        Assert.Equal(EnvironmentModel, await ReadStoredAsync(context));

        var seedLine = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("seeded the Ollama model"));
        Assert.Contains("inert", seedLine.Message);
        Assert.Contains("no effect", seedLine.Message);
    }

    /// <summary>
    /// With no environment value the compiled-in default is written rather than nothing — the same
    /// reasoning as the base URL's, and the reason the AI section never needs a "not configured"
    /// branch. Leaving the row absent would show an empty picker while classification quietly ran
    /// against a model the operator could not see, which is the invisibility #112 exists to end.
    /// </summary>
    [Fact]
    public async Task Seeds_the_built_in_default_when_the_environment_supplies_nothing()
    {
        using var context = CreateContext();
        var logger = new RecordingLogger();

        await OllamaModelSeeder.SeedAsync(context, environmentModel: null, logger);

        Assert.Equal(OllamaModelResolver.DefaultModel, await ReadStoredAsync(context));
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("built-in default"));
    }

    /// <summary>
    /// <b>THE UPGRADE ASSERTION.</b> An installation that already ran #89 has a base URL row and no
    /// model row. The model seed must not be gated on the base URL's absence, or exactly those
    /// installations — every existing deployment — would never get a model row and the new setting
    /// would be permanently invisible on them.
    /// </summary>
    [Fact]
    public async Task Seeds_a_model_row_on_an_upgrade_that_already_has_a_base_url_row()
    {
        using var context = CreateContext();

        // The state #89 left behind: a base URL row, no model row.
        await OllamaBaseUrlSeeder.SeedAsync(context, "http://192.0.2.30:11434", new RecordingLogger());
        Assert.Null(await ReadStoredAsync(context));

        await OllamaModelSeeder.SeedAsync(context, environmentModel: null, new RecordingLogger());

        Assert.Equal(OllamaModelResolver.DefaultModel, await ReadStoredAsync(context));
        // And the base URL row is untouched: the two seeders own separate rows.
        Assert.Equal(
            "http://192.0.2.30:11434",
            await context.Settings.AsNoTracking()
                .Where(e => e.Name == SettingKey.OllamaBaseUrl.ToString())
                .Select(e => e.Value)
                .FirstOrDefaultAsync());
    }

    /// <summary>
    /// <b>THE SEED-ONCE ASSERTION.</b> A second start must not revert an operator's choice, even
    /// when the environment still carries the value the row was originally seeded from.
    /// </summary>
    [Fact]
    public async Task Does_not_re_seed_or_overwrite_a_row_the_operator_changed()
    {
        using var context = CreateContext();

        await OllamaModelSeeder.SeedAsync(context, EnvironmentModel, new RecordingLogger());

        // The operator then picks a different model on the Settings page.
        var row = await context.Settings.SingleAsync(e => e.Name == SettingKey.OllamaModel.ToString());
        row.Value = OperatorModel;
        await context.SaveChangesAsync();

        // A later restart, with the environment variable still set to its original value.
        await OllamaModelSeeder.SeedAsync(context, EnvironmentModel, new RecordingLogger());

        Assert.Equal(OperatorModel, await ReadStoredAsync(context));
        // And exactly one row: a second seed must not add a duplicate either.
        Assert.Equal(
            1,
            await context.Settings.CountAsync(e => e.Name == SettingKey.OllamaModel.ToString()));
    }

    [Fact]
    public async Task Warns_when_the_environment_diverges_from_the_stored_value()
    {
        using var context = CreateContext();
        await OllamaModelSeeder.SeedAsync(context, OperatorModel, new RecordingLogger());

        var logger = new RecordingLogger();
        await OllamaModelSeeder.SeedAsync(context, EnvironmentModel, logger);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(OperatorModel, warning.Message, StringComparison.Ordinal);
        Assert.Contains("in force", warning.Message);
        Assert.Equal(OperatorModel, await ReadStoredAsync(context));
    }

    [Fact]
    public async Task Does_not_warn_when_the_environment_agrees_with_the_stored_value()
    {
        using var context = CreateContext();
        await OllamaModelSeeder.SeedAsync(context, EnvironmentModel, new RecordingLogger());

        var logger = new RecordingLogger();
        await OllamaModelSeeder.SeedAsync(context, EnvironmentModel, logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task An_absent_environment_variable_is_a_non_event()
    {
        using var context = CreateContext();
        await OllamaModelSeeder.SeedAsync(context, OperatorModel, new RecordingLogger());

        var logger = new RecordingLogger();
        await OllamaModelSeeder.SeedAsync(context, environmentModel: null, logger);

        Assert.Equal(OperatorModel, await ReadStoredAsync(context));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// <b>THE SECOND WRITE PATH IS VALIDATED.</b> The environment variable reaches the same row as
    /// PUT /api/admin/ai/ollama, so a value the PUT would reject must not get in through the seeder
    /// either. Each case is a real paste accident that would otherwise seed a row failing every
    /// /api/chat call while the settings page showed a value that looked right.
    /// </summary>
    [Theory]
    [InlineData("qwen2.5:7b ")]
    [InlineData("qwen2.5:7b\n")]
    [InlineData("qwen 2.5:7b")]
    public async Task Rejects_an_unusable_environment_value_and_seeds_the_default_instead(string environmentModel)
    {
        using var context = CreateContext();
        var logger = new RecordingLogger();

        await OllamaModelSeeder.SeedAsync(context, environmentModel, logger);

        Assert.Equal(OllamaModelResolver.DefaultModel, await ReadStoredAsync(context));

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Arbitarr:Ai:Ollama:Model", warning.Message, StringComparison.Ordinal);
        Assert.Contains("was NOT stored", warning.Message, StringComparison.Ordinal);

        // The seed line must not claim the environment was honoured when it was rejected.
        var seedLine = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("seeded the Ollama model"));
        Assert.Contains("built-in default", seedLine.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blank environment value is "the operator set nothing", not "the operator set something
    /// wrong": it takes the default silently rather than warning. Warning here would put a scary
    /// line in front of every operator who never set the variable at all.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_environment_value_seeds_the_default_without_a_warning(string environmentModel)
    {
        using var context = CreateContext();
        var logger = new RecordingLogger();

        await OllamaModelSeeder.SeedAsync(context, environmentModel, logger);

        Assert.Equal(OllamaModelResolver.DefaultModel, await ReadStoredAsync(context));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// A rejected value is malformed by definition — that is why it was rejected — and it must not
    /// reach the log store served at /api/admin/logs, where a control character would land in a
    /// page rendering those logs. Neither the value nor
    /// <c>SettingsValidationException.Message</c> (which quotes it) may be logged.
    ///
    /// <para><b>POSITIVE CONTROL (CLAUDE.md §4).</b> An absence assertion passes just as happily
    /// when the needle was never in play, so this first proves the assertion CAN fail: the same
    /// needle is planted in a value the seeder DOES log — a VALID model name, which reaches the
    /// seeded-value slot — and the search is shown to find it there. Only then is the rejection path
    /// asserted clean.</para>
    /// </summary>
    [Fact]
    public async Task A_rejected_environment_value_is_not_echoed()
    {
        const string Needle = "placeholder-model-canary-a1b2c3";

        // POSITIVE CONTROL, on its own database so its row does not turn the run under test into
        // the divergence branch.
        var controlPath = Path.Combine(Path.GetTempPath(), $"arbitarr-112-control-{Guid.NewGuid():N}.db");
        try
        {
            using var control = CreateContext(controlPath);
            var controlLogger = new RecordingLogger();
            await OllamaModelSeeder.SeedAsync(control, Needle, controlLogger);

            Assert.Contains(controlLogger.Entries, e => e.Message.Contains(Needle, StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(controlPath))
            {
                File.Delete(controlPath);
            }
        }

        using var context = CreateContext();
        var logger = new RecordingLogger();

        // The same needle, now inside a value the validator rejects.
        await OllamaModelSeeder.SeedAsync(context, $"{Needle} with spaces", logger);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(Needle, StringComparison.Ordinal));
        Assert.Equal(OllamaModelResolver.DefaultModel, await ReadStoredAsync(context));
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
