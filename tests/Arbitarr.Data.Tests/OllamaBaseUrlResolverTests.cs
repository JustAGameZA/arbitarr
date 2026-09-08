using Arbitarr.Core.Ai;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #89: <see cref="OllamaBaseUrlResolver"/>, the read side of the "no restart" seam.
///
/// <para>The consumer of this value does <c>new Uri(...)</c> with it on the classification path, so
/// the property that matters most here is that an UNUSABLE ROW DEGRADES rather than throws. Both
/// writers validate, so such a row should be unreachable — but if one ever slipped through (a row
/// predating the validation, or edited directly in the database) a throwing resolver would fail
/// every classification, and would keep failing after the operator corrected the setting, because
/// the failure happens on the way to reading the corrected value.</para>
/// </summary>
public sealed class OllamaBaseUrlResolverTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"arbitarr-89-resolver-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private async Task StoreAsync(ArbitarrDbContext context, string value)
    {
        context.Settings.Add(new SettingEntry
        {
            Name = SettingKey.OllamaBaseUrl.ToString(),
            Value = value,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Serves_the_stored_value()
    {
        using var context = CreateContext();
        await StoreAsync(context, "http://ollama.example.com:11434");

        var resolver = new OllamaBaseUrlResolver(context, new OllamaBaseUrlCache());

        Assert.Equal("http://ollama.example.com:11434", await resolver.GetAsync());
    }

    [Fact]
    public async Task Falls_back_to_the_default_when_no_row_exists()
    {
        using var context = CreateContext();

        var resolver = new OllamaBaseUrlResolver(context, new OllamaBaseUrlCache());

        Assert.Equal(OllamaBaseUrlResolver.DefaultBaseUrl, await resolver.GetAsync());
    }

    /// <summary>
    /// <b>THE DEGRADE-DO-NOT-THROW ASSERTION.</b> Each of these would otherwise reach
    /// <c>new Uri(...)</c> on the classification path. Returning the default keeps the instance
    /// classifying and leaves the row correctable through the UI.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("/api/chat")]
    [InlineData("ftp://ollama.example.com")]
    public async Task Falls_back_to_the_default_when_the_stored_row_is_unusable(string stored)
    {
        using var context = CreateContext();
        await StoreAsync(context, stored);

        var resolver = new OllamaBaseUrlResolver(context, new OllamaBaseUrlCache());

        var resolved = await resolver.GetAsync();

        Assert.Equal(OllamaBaseUrlResolver.DefaultBaseUrl, resolved);
        // And the value it returns is itself usable, which is the whole point of the fallback:
        // the caller builds a Uri from this.
        Assert.True(Uri.TryCreate(resolved, UriKind.Absolute, out _));
    }

    /// <summary>
    /// <b>THE "NO RESTART" ASSERTION, end to end through the read side.</b> After a write
    /// invalidates the cache the next resolution reflects the new row — without this, a saved
    /// address would not take effect until the process restarted, and every test that only checks
    /// the row was written would still pass.
    /// </summary>
    [Fact]
    public async Task Reflects_a_new_value_after_the_cache_is_invalidated()
    {
        using var context = CreateContext();
        await StoreAsync(context, "http://first.example.com:11434");

        var cache = new OllamaBaseUrlCache();
        var resolver = new OllamaBaseUrlResolver(context, cache);
        Assert.Equal("http://first.example.com:11434", await resolver.GetAsync());

        var row = await context.Settings.SingleAsync(e => e.Name == SettingKey.OllamaBaseUrl.ToString());
        row.Value = "http://second.example.com:11434";
        await context.SaveChangesAsync();

        // Still the old value: nothing has told the cache the row moved.
        Assert.Equal("http://first.example.com:11434", await resolver.GetAsync());

        cache.Invalidate();

        Assert.Equal("http://second.example.com:11434", await resolver.GetAsync());
    }
}
