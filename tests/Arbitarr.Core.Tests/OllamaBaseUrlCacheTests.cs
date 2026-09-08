using Arbitarr.Core.Ai;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// #89: the cache that makes a base-URL change take effect WITHOUT A RESTART.
///
/// <para>The behaviour under test is small but load-bearing, and its failure mode is silent: if
/// <see cref="OllamaBaseUrlCache.Invalidate"/> stops causing a re-read, every test that merely
/// checks the value was persisted still passes while the running process keeps calling the old
/// address until it is restarted. So the assertions here are about the STALENESS TRANSITIONS, not
/// about the stored string.</para>
///
/// <para>Everything is driven through <see cref="OllamaBaseUrlCache.GetCurrentIfFresh"/> and
/// <see cref="OllamaBaseUrlCache.TrySetIfGenerationMatches"/> — the pair production actually uses.
/// An earlier revision of these tests drove an unguarded <c>SetIfStale</c>/<c>Current</c>/
/// <c>IsStale</c> surface that NO production code called, which meant this file was pinning an API
/// nobody depended on while the real resolution path went untested through it. That surface has
/// been deleted; if a test here ever needs a publish helper that skips the generation check, the
/// hazard it reintroduces is described on the type.</para>
/// </summary>
public sealed class OllamaBaseUrlCacheTests
{
    private const string First = "http://ollama.example.com:11434";
    private const string Second = "http://other.example.com:11434";

    /// <summary>
    /// Publishes a value the way the resolver does: read the (stale) state to take a generation,
    /// then publish against it. Returns the value now in force.
    /// </summary>
    private static string Publish(OllamaBaseUrlCache cache, string baseUrl)
    {
        cache.GetCurrentIfFresh(out var generation);
        Assert.True(cache.TrySetIfGenerationMatches(baseUrl, generation));
        return baseUrl;
    }

    /// <summary>
    /// Starts stale, so the first use after start-up reads the row rather than serving a value
    /// nobody has loaded. A cache that started fresh-and-empty would serve null on first use.
    /// </summary>
    [Fact]
    public void A_new_cache_is_stale_and_holds_nothing()
    {
        var cache = new OllamaBaseUrlCache();

        Assert.Null(cache.GetCurrentIfFresh(out _));
    }

    [Fact]
    public void Publishing_a_value_clears_the_stale_flag()
    {
        var cache = new OllamaBaseUrlCache();

        Publish(cache, First);

        Assert.Equal(First, cache.GetCurrentIfFresh(out _));
    }

    /// <summary>
    /// <b>THE "NO RESTART" ASSERTION.</b> After an invalidation the next read is accepted, so a
    /// settings write is visible to the very next use. Removing the Invalidate() call in the write
    /// path is exactly what this catches.
    /// </summary>
    [Fact]
    public void Invalidating_makes_the_next_read_win()
    {
        var cache = new OllamaBaseUrlCache();
        Publish(cache, First);

        cache.Invalidate();

        Assert.Null(cache.GetCurrentIfFresh(out _));
        Publish(cache, Second);
        Assert.Equal(Second, cache.GetCurrentIfFresh(out _));
    }

    /// <summary>
    /// A resolver can read the database just before a settings write commits. That write
    /// invalidates the cache, so the pre-write read must not be allowed to republish the old URL
    /// afterwards and pin it until another update.
    /// </summary>
    [Fact]
    public void A_read_started_before_invalidation_cannot_publish_afterwards()
    {
        var cache = new OllamaBaseUrlCache();
        Publish(cache, First);

        Assert.Equal(First, cache.GetCurrentIfFresh(out var generation));
        cache.Invalidate();

        Assert.False(cache.TrySetIfGenerationMatches(First, generation));
        Assert.Null(cache.GetCurrentIfFresh(out _));

        Publish(cache, Second);
        Assert.Equal(Second, cache.GetCurrentIfFresh(out _));
    }

    /// <summary>
    /// Two invalidations in a row must not let a read taken between them publish: the generation
    /// advances on EVERY invalidation, not only on the first after a publish. A counter that
    /// advanced only when the cache was fresh would leave this window open.
    /// </summary>
    [Fact]
    public void A_second_invalidation_also_advances_the_generation()
    {
        var cache = new OllamaBaseUrlCache();
        Publish(cache, First);

        cache.Invalidate();
        cache.GetCurrentIfFresh(out var generation);
        cache.Invalidate();

        Assert.False(cache.TrySetIfGenerationMatches(First, generation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_value_is_rejected_rather_than_published(string value)
    {
        var cache = new OllamaBaseUrlCache();
        cache.GetCurrentIfFresh(out var generation);

        Assert.ThrowsAny<ArgumentException>(() => cache.TrySetIfGenerationMatches(value, generation));
        Assert.Null(cache.GetCurrentIfFresh(out _));
    }
}
