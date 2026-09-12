using Arbitarr.Core.Diagnostics;
using Arbitarr.Data.Media;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-6l9b.1: <see cref="RadarrCredentialProvider"/> is the SINGLE production reader of the stored
/// Radarr API key, and it returns null for the half-configured states rather than a credential with a
/// missing half.
/// </summary>
/// <remarks>
/// The half-configured cases are the substance. "No address" and "address but no key" are both
/// reported as null because every consumer wants the same thing from them: the probe would send an
/// unauthenticated request that Radarr answers 401 — recording a failure against a service that is
/// not broken — and a library reader would render an error where "not configured" is the truthful
/// answer. Asserting them here means a consumer cannot be handed a <see cref="RadarrCredential"/>
/// carrying an empty key.
/// </remarks>
public sealed class RadarrCredentialProviderTests : IDisposable
{
    private const string BaseUrl = "http://radarr.example:7878/";
    private const string ApiKey = "placeholder-radarr-key-7b0c25ea";

    private readonly SqliteTestDatabase _database = new("arbitarr-radarr-credential");

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
    public async Task A_fully_configured_instance_yields_a_credential()
    {
        await using var context = CreateContext();
        var repository = new RadarrInstanceRepository(context);
        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var credential = await new RadarrCredentialProvider(repository).GetAsync(CancellationToken.None);

        Assert.NotNull(credential);
        Assert.Equal(new Uri(BaseUrl), credential!.BaseUrl);
        Assert.Equal(ApiKey, credential.ApiKey);
    }

    [Fact]
    public async Task An_unconfigured_instance_yields_null()
    {
        await using var context = CreateContext();
        var repository = new RadarrInstanceRepository(context);
        var provider = new RadarrCredentialProvider(repository);

        Assert.Null(await provider.GetAsync(CancellationToken.None));

        // POSITIVE CONTROL: the same provider against the same store DOES produce a credential once
        // one is configured, so the null above is an unconfigured instance rather than a provider
        // that never returns anything.
        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);
        Assert.NotNull(await provider.GetAsync(CancellationToken.None));
    }

    /// <summary>
    /// THE HALF-CONFIGURED STATE: an address with no key is null, not a credential with an empty key.
    /// The positive control is the address itself — it really IS stored — so the null is about the
    /// missing key rather than about an instance that was never configured at all.
    /// </summary>
    [Fact]
    public async Task An_address_with_no_key_yields_null_rather_than_an_empty_credential()
    {
        await using var context = CreateContext();
        var repository = new RadarrInstanceRepository(context);
        await repository.SetAsync(BaseUrl, apiKey: null, CancellationToken.None);

        // POSITIVE CONTROL: the address is stored and the key is not.
        Assert.Equal(BaseUrl, await repository.GetBaseUrlAsync(CancellationToken.None));
        Assert.False(await repository.HasApiKeyAsync(CancellationToken.None));

        Assert.Null(await new RadarrCredentialProvider(repository).GetAsync(CancellationToken.None));
    }

    /// <summary>
    /// THE KEY IS REDACTED FROM <see cref="RadarrCredential.ToString"/>.
    ///
    /// <para>A positional record's synthesised <c>ToString</c> renders every positional member, so
    /// without the override this type would print its own key — and it is handed to code about to
    /// make a network request, which is the code most likely to reach a log line or an exception
    /// message. Neither existing layer covers that shape: <c>IHttpClientFactory</c>'s redaction
    /// collapses a URI's query string and <c>LogMessageCleanser</c> scrubs query strings but not
    /// paths, while a bare <c>ApiKey = value</c> inside a record's string form is not a URI at all
    /// (CLAUDE.md §1).</para>
    ///
    /// <para><b>POSITIVE CONTROL (CLAUDE.md §4):</b> the redaction marker must BE PRESENT. That is
    /// what proves the key reached the formatter and was replaced there, rather than the absence
    /// below passing because <c>ToString</c> returned something empty, or the type name alone, or
    /// because the planted key was never in play. The key is planted through the real provider, so
    /// the value under test is the one a consumer actually receives.</para>
    /// </summary>
    [Fact]
    public async Task The_credential_redacts_its_key_when_rendered_as_a_string()
    {
        await using var context = CreateContext();
        var repository = new RadarrInstanceRepository(context);
        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var credential = await new RadarrCredentialProvider(repository).GetAsync(CancellationToken.None);
        Assert.NotNull(credential);

        // The key really is in play: the credential carries it, so a formatter that printed its
        // members verbatim WOULD have it to print.
        Assert.Equal(ApiKey, credential!.ApiKey);

        var rendered = credential.ToString();

        // POSITIVE CONTROL: the redaction fired.
        Assert.Contains(CredentialPatterns.Replacement, rendered, StringComparison.Ordinal);
        // ...therefore this absence is a real redaction rather than an empty or truncated render.
        Assert.DoesNotContain(ApiKey, rendered, StringComparison.OrdinalIgnoreCase);

        // The address is still rendered in full — it is not a credential, and printing it is what
        // makes the override useful for diagnostics rather than merely silent.
        Assert.Contains(BaseUrl, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same redaction through an interpolated string, which is the shape a log call actually
    /// takes — <c>$"{credential}"</c> and a structured-logging argument both route through
    /// <c>ToString</c>, but asserting only the direct call would leave a reader wondering whether the
    /// interpolation path differs. It does not, and this pins that.
    /// </summary>
    [Fact]
    public async Task The_redaction_holds_when_the_credential_is_interpolated()
    {
        await using var context = CreateContext();
        var repository = new RadarrInstanceRepository(context);
        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var credential = await new RadarrCredentialProvider(repository).GetAsync(CancellationToken.None);
        Assert.NotNull(credential);

        var line = $"probing {credential}";

        // POSITIVE CONTROL: the marker is present, so the credential really was formatted into the
        // line rather than the interpolation producing nothing.
        Assert.Contains(CredentialPatterns.Replacement, line, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The provider reads PER CALL rather than capturing at construction, so a key configured after
    /// the provider was built is seen. A value captured once would be absent forever for anyone who
    /// configures Radarr after boot — which is everyone, on a first run.
    /// </summary>
    [Fact]
    public async Task The_credential_is_read_per_call_so_a_later_write_is_seen()
    {
        await using var context = CreateContext();
        var repository = new RadarrInstanceRepository(context);
        var provider = new RadarrCredentialProvider(repository);

        Assert.Null(await provider.GetAsync(CancellationToken.None));

        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var credential = await provider.GetAsync(CancellationToken.None);
        Assert.NotNull(credential);
        Assert.Equal(ApiKey, credential!.ApiKey);

        // And a repoint is seen too, through the same provider instance.
        await repository.SetAsync("http://radarr-moved.example:7878/", ApiKey, CancellationToken.None);
        var repointed = await provider.GetAsync(CancellationToken.None);
        Assert.NotNull(repointed);
        Assert.Equal(new Uri("http://radarr-moved.example:7878/"), repointed!.BaseUrl);
    }
}
