using Arbitarr.Core.Ai;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Settings;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #112: <see cref="OllamaModelResolver"/>, the read side of the model's "no restart" seam and the
/// deliberate sibling of <see cref="OllamaBaseUrlResolverTests"/>.
///
/// <para>The property that matters most here is the same one: an UNUSABLE ROW DEGRADES rather than
/// throws. Both writers validate, so such a row should be unreachable — but a row predating the
/// validation, or edited directly in the database, must not fail every classification with no way
/// for the operator's correction to be read.</para>
///
/// <para>The second property is the one #112 exists for: after the cache is invalidated the next
/// resolution reflects the new row. Without it a saved model would not take effect until the
/// process restarted, and every test that only checks the row was written would still pass — which
/// is exactly the shape of the bug this issue reports against the model.</para>
/// </summary>
public sealed class OllamaModelResolverTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new("arbitarr-112-model-resolver");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static async Task StoreAsync(ArbitarrDbContext context, string value)
    {
        context.Settings.Add(new SettingEntry
        {
            Name = SettingKey.OllamaModel.ToString(),
            Value = value,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Serves_the_stored_value()
    {
        using var context = CreateContext();
        await StoreAsync(context, "llama3.1:8b");

        var resolver = new OllamaModelResolver(context, new OllamaModelCache());

        Assert.Equal("llama3.1:8b", await resolver.GetAsync());
    }

    [Fact]
    public async Task Falls_back_to_the_default_when_no_row_exists()
    {
        using var context = CreateContext();

        var resolver = new OllamaModelResolver(context, new OllamaModelCache());

        Assert.Equal(OllamaModelResolver.DefaultModel, await resolver.GetAsync());
    }

    /// <summary>
    /// <b>THE DEGRADE-DO-NOT-THROW ASSERTION.</b> A blank row would otherwise be sent as Ollama's
    /// <c>model</c> field and fail every call, with the failure happening on the way to reading the
    /// operator's corrected value.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Falls_back_to_the_default_when_the_stored_row_is_unusable(string stored)
    {
        using var context = CreateContext();
        await StoreAsync(context, stored);

        var resolver = new OllamaModelResolver(context, new OllamaModelCache());

        var resolved = await resolver.GetAsync();

        Assert.Equal(OllamaModelResolver.DefaultModel, resolved);
        Assert.False(string.IsNullOrWhiteSpace(resolved));
    }

    /// <summary>
    /// The read-side check is deliberately NARROWER than the write-side validator: it asks only
    /// "can this be sent", so a legacy row breaching the write-time policy still resolves to itself
    /// rather than being silently replaced by the default. Substituting a different model here would
    /// classify against something the operator never chose, and the settings page would keep showing
    /// the row while the calls used something else.
    /// </summary>
    [Fact]
    public async Task A_legacy_row_that_the_write_path_would_now_reject_still_resolves_to_itself()
    {
        var legacy = new string('m', SettingsValidator.OllamaModelMaxLength + 50);
        using var context = CreateContext();
        await StoreAsync(context, legacy);

        // Existence control: the value really would be rejected by the write path today, so this
        // test is genuinely about a row the validator disagrees with.
        Assert.Throws<SettingsValidationException>(() => SettingsValidator.ValidateOllamaModel(legacy));

        var resolver = new OllamaModelResolver(context, new OllamaModelCache());

        Assert.Equal(legacy, await resolver.GetAsync());
    }

    /// <summary>
    /// <b>THE "NO RESTART" ASSERTION, end to end through the read side.</b> Without the
    /// invalidation, a saved model would not take effect until the process restarted.
    /// </summary>
    [Fact]
    public async Task Reflects_a_new_value_after_the_cache_is_invalidated()
    {
        using var context = CreateContext();
        await StoreAsync(context, "llama3.1:8b");

        var cache = new OllamaModelCache();
        var resolver = new OllamaModelResolver(context, cache);
        Assert.Equal("llama3.1:8b", await resolver.GetAsync());

        var row = await context.Settings.SingleAsync(e => e.Name == SettingKey.OllamaModel.ToString());
        row.Value = "phi4:14b";
        await context.SaveChangesAsync();

        // Still the old value: nothing has told the cache the row moved.
        Assert.Equal("llama3.1:8b", await resolver.GetAsync());

        cache.Invalidate();

        Assert.Equal("phi4:14b", await resolver.GetAsync());
    }

    /// <summary>
    /// The synchronous accessor exists for the <c>AiModelIdentity</c> DI factory, which has no
    /// await. It must agree with <see cref="OllamaModelResolver.GetAsync"/> on every input, or the
    /// verdict cache key would be computed from one model while the call used another — which is
    /// the R17 invalidation failing silently rather than loudly.
    /// </summary>
    [Fact]
    public async Task The_synchronous_read_agrees_with_the_asynchronous_one()
    {
        using var context = CreateContext();
        await StoreAsync(context, "phi4:14b");

        Assert.Equal("phi4:14b", new OllamaModelResolver(context, new OllamaModelCache()).Get());
        Assert.Equal("phi4:14b", await new OllamaModelResolver(context, new OllamaModelCache()).GetAsync());

        // And on the fallback path too, where the two could most easily have diverged.
        var row = await context.Settings.SingleAsync(e => e.Name == SettingKey.OllamaModel.ToString());
        row.Value = "  ";
        await context.SaveChangesAsync();

        Assert.Equal(
            OllamaModelResolver.DefaultModel,
            new OllamaModelResolver(context, new OllamaModelCache()).Get());
        Assert.Equal(
            OllamaModelResolver.DefaultModel,
            await new OllamaModelResolver(context, new OllamaModelCache()).GetAsync());
    }

    /// <summary>
    /// The model and the base URL are INDEPENDENT rows with independent caches. Invalidating one
    /// must not disturb the other — a shared generation counter (the tidy-up
    /// <see cref="OllamaModelCache"/>'s doc records as rejected) would have made every model write
    /// force a base-URL re-read and vice versa.
    /// </summary>
    [Fact]
    public async Task Invalidating_the_model_cache_leaves_the_base_url_cache_alone()
    {
        using var context = CreateContext();
        await StoreAsync(context, "llama3.1:8b");
        context.Settings.Add(new SettingEntry
        {
            Name = SettingKey.OllamaBaseUrl.ToString(),
            Value = "http://ollama.example.com:11434",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var modelCache = new OllamaModelCache();
        var baseUrlCache = new OllamaBaseUrlCache();
        var modelResolver = new OllamaModelResolver(context, modelCache);
        var baseUrlResolver = new OllamaBaseUrlResolver(context, baseUrlCache);

        Assert.Equal("llama3.1:8b", await modelResolver.GetAsync());
        Assert.Equal("http://ollama.example.com:11434", await baseUrlResolver.GetAsync());

        // Move BOTH rows, then invalidate only the model cache.
        var modelRow = await context.Settings.SingleAsync(e => e.Name == SettingKey.OllamaModel.ToString());
        modelRow.Value = "phi4:14b";
        var urlRow = await context.Settings.SingleAsync(e => e.Name == SettingKey.OllamaBaseUrl.ToString());
        urlRow.Value = "http://ollama.example.com:9999";
        await context.SaveChangesAsync();

        modelCache.Invalidate();

        Assert.Equal("phi4:14b", await modelResolver.GetAsync());
        // The base URL cache was never told, so it is still serving its own cached value — proving
        // the two are not sharing a generation.
        Assert.Equal("http://ollama.example.com:11434", await baseUrlResolver.GetAsync());
    }
}
