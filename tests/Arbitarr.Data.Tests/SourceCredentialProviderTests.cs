using Arbitarr.Core.Diagnostics;
using Arbitarr.Data.Sources;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-x7w8.3: <see cref="SourceCredentialProvider"/> is the single production reader of a stored
/// per-source API key (ADR 0018), so these prove the two things that make that indirection worth
/// having — it actually reads the key, and it declines to produce a credential for a
/// half-configured source.
/// </summary>
/// <remarks>
/// <para><b>WHY THE POSITIVE CONTROL MATTERS HERE SPECIFICALLY.</b>
/// <c>SecretReaderSingleCallerTests</c> counts call sites of
/// <c>ReadApiKeyForUpstreamRequestAsync</c> and asserts the provider is the only one. That scan
/// passes just as happily against a provider that returns <see langword="null"/> unconditionally —
/// the call site would still be there, in the sanctioned file, and nothing would ever reach an
/// upstream. <see cref="The_provider_returns_the_key_that_was_planted"/> is what makes the scan
/// mean something: it plants a known key and asserts the provider hands back that exact value, so
/// a provider that stopped reading fails here rather than passing quietly there.</para>
///
/// <para>The half-configured cases are asserted SEPARATELY rather than as "some half-configured
/// state returns null", because they fail for different reasons — an absent row never reaches the
/// key read at all, while a keyed-but-unkeyed source reaches it and gets nothing back — and one
/// assertion covering both would keep passing if either branch regressed into the other.</para>
/// </remarks>
public sealed class SourceCredentialProviderTests : IDisposable
{
    private const string BaseUrl = "http://192.0.2.30:5076";

    private readonly SqliteTestDatabase _database = new("arr-searcher-source-credentials-test");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public async Task The_provider_returns_the_key_that_was_planted()
    {
        // The positive control for the single-caller scan: prove the sanctioned call site actually
        // reads. A planted key must come back byte-for-byte, because the whole point of the provider
        // is to be the one place that turns a stored secret into a value a consumer may send
        // upstream — a provider that returned null always would satisfy every absence assertion in
        // the suite while quietly breaking every outbound request.
        const string PlantedKey = "planted-source-key-6f2b41d9";

        using var context = CreateContext();
        var repository = new SourceRepository(context);
        var source = await repository.AddAsync(
            SourceRepository.NzbHydraKind, "Keyed source", BaseUrl, PlantedKey, enabled: true, CancellationToken.None);

        var provider = new SourceCredentialProvider(repository);
        var credential = await provider.GetAsync(source.Id, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.Equal(PlantedKey, credential!.ApiKey);
        Assert.Equal(BaseUrl, credential.BaseUrl);
    }

    [Fact]
    public async Task The_credentials_ToString_redacts_the_key_rather_than_printing_it()
    {
        // A positional record's synthesised ToString prints every member by value, so without the
        // override this type renders its own ApiKey — and an interpolation like $"probe failed for
        // {credential}" compiles, reads as harmless, and lands verbatim in the persistent log store.
        // Neither mechanism that would normally catch this applies: IHttpClientFactory's URI
        // redaction and LogMessageCleanser both scrub QUERY STRINGS, and a bare "ApiKey = …" is in
        // neither shape (CLAUDE.md §1).
        const string PlantedKey = "planted-tostring-key-3e7c05af";

        using var context = CreateContext();
        var repository = new SourceRepository(context);
        var source = await repository.AddAsync(
            SourceRepository.NzbHydraKind, "Rendered source", BaseUrl, PlantedKey, enabled: true, CancellationToken.None);

        var provider = new SourceCredentialProvider(repository);
        var credential = await provider.GetAsync(source.Id, CancellationToken.None);
        Assert.NotNull(credential);

        var rendered = credential!.ToString();

        // THE POSITIVE CONTROL, and the assertion that must come first: the redaction marker is
        // PRESENT. Asserting only that the key is absent would pass just as happily against a
        // ToString that returned "" or the bare type name — an empty string contains no secret
        // either — so the marker's presence is what proves the key reached this renderer and was
        // replaced, rather than never arriving (CLAUDE.md §4, the LogSecretInjectionTests shape).
        Assert.Contains(CredentialPatterns.Replacement, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedKey, rendered, StringComparison.Ordinal);

        // The address is still rendered in full: a redaction that erased everything would satisfy
        // both assertions above while making the value useless for telling which source failed.
        Assert.Contains(BaseUrl, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_with_a_base_url_but_no_key_yields_no_credential()
    {
        // Half-configured case one: the row exists and its address is valid, but no key was ever
        // entered. Reported as null rather than as a credential with an empty key, so no consumer
        // sends an unauthenticated request that the source answers 401 — which would record an
        // authentication failure against a source whose key is merely absent.
        using var context = CreateContext();
        var repository = new SourceRepository(context);
        var source = await repository.AddAsync(
            SourceRepository.NzbHydraKind, "Unkeyed source", BaseUrl, apiKey: null, enabled: true, CancellationToken.None);

        // The planted-key test above is what proves this null is a real refusal rather than a
        // provider that never reads anything: same provider, same database, and it DID return a
        // credential when a key was present.
        Assert.False(await repository.HasApiKeyAsync(source.Id, CancellationToken.None));

        var provider = new SourceCredentialProvider(repository);
        var credential = await provider.GetAsync(source.Id, CancellationToken.None);

        Assert.Null(credential);
    }

    [Fact]
    public async Task A_source_that_does_not_exist_yields_no_credential()
    {
        // Half-configured case two, and a different code path from the one above: there is no row,
        // so there is no base URL, and the provider must stop before the key read rather than
        // producing a credential with an empty or null address that a consumer would then try to
        // build a request URI from.
        using var context = CreateContext();
        var repository = new SourceRepository(context);
        var provider = new SourceCredentialProvider(repository);

        var credential = await provider.GetAsync(sourceId: 4242, CancellationToken.None);

        Assert.Null(credential);
    }

    [Fact]
    public async Task A_key_stored_for_one_source_is_not_handed_out_for_another()
    {
        // The per-source key is namespaced by id (source:{id}:api_key). A provider that ignored the
        // id would return the wrong indexer's key to the wrong upstream — which with N indexers is
        // a disclosure, not merely a bug — and every other test here would still pass, because each
        // of them exercises exactly one source.
        const string FirstKey = "first-source-key-a1b2c3";
        const string SecondKey = "second-source-key-d4e5f6";

        using var context = CreateContext();
        var repository = new SourceRepository(context);
        var first = await repository.AddAsync(
            SourceRepository.NzbHydraKind, "First source", BaseUrl, FirstKey, enabled: true, CancellationToken.None);
        var second = await repository.AddAsync(
            SourceRepository.NzbHydraKind, "Second source", "http://192.0.2.31:5076", SecondKey, enabled: true, CancellationToken.None);

        var provider = new SourceCredentialProvider(repository);

        var firstCredential = await provider.GetAsync(first.Id, CancellationToken.None);
        var secondCredential = await provider.GetAsync(second.Id, CancellationToken.None);

        Assert.NotNull(firstCredential);
        Assert.NotNull(secondCredential);
        Assert.Equal(FirstKey, firstCredential!.ApiKey);
        Assert.Equal(SecondKey, secondCredential!.ApiKey);
    }
}
