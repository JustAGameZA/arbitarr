using Arbitarr.Core.Diagnostics;
using Arbitarr.Data.Media;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-1ox9: <see cref="SonarrCredential"/> redacts its key when rendered as a string, in every
/// formatting shape a caller can reach, and without disturbing the record's value semantics.
/// </summary>
/// <remarks>
/// <para>The key is planted THROUGH THE REAL PROVIDER in every case, so the value under test is the
/// one a consumer actually receives rather than a hand-constructed record that might differ from it.</para>
///
/// <para><b>Every absence assertion here is preceded by a positive control</b> (CLAUDE.md §4):
/// <c>Assert.DoesNotContain(ApiKey, rendered)</c> passes just as happily against an empty string, a
/// null, or a bare type name. The marker's PRESENCE is what proves the key reached the formatter and
/// was replaced there.</para>
/// </remarks>
public sealed class SonarrCredentialProviderTests : IDisposable
{
    private const string BaseUrl = "http://sonarr.example:8989/";

    /// <summary>
    /// Follows the existing fixture convention (which the pre-commit secret guard allowlists) with a
    /// distinctive suffix, so a log-row search in the integration counterpart can find exactly this
    /// value and nothing else.
    /// </summary>
    private const string ApiKey = "placeholder-sonarr-key-1ox9d4e7";

    private readonly SqliteTestDatabase _database = new("arbitarr-sonarr-credential");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private async Task<SonarrCredential> PlantAndReadCredentialAsync(ArbitarrDbContext context)
    {
        var repository = new ArrInstanceRepository(context);
        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var credential = await new SonarrCredentialProvider(repository).GetAsync(CancellationToken.None);
        Assert.NotNull(credential);

        // THE KEY IS IN PLAY: the credential really carries it, so a formatter printing its members
        // verbatim WOULD have it to print. Without this, every redaction assertion below could be
        // satisfied by a credential that never held the key at all.
        Assert.Equal(ApiKey, credential!.ApiKey);

        return credential;
    }

    /// <summary>
    /// Asserts the four facts every rendering shape must satisfy, in the order that makes them bite:
    /// the marker is present (so the redaction FIRED), therefore the key's absence is a real
    /// redaction, and the address is still rendered in full.
    /// </summary>
    private static void AssertRedactedRendering(string rendered)
    {
        // POSITIVE CONTROL: the redaction fired. Not "the key is absent" — an empty string, a null,
        // or a bare type name contains no secret either (CLAUDE.md §4).
        Assert.Contains(CredentialPatterns.Replacement, rendered, StringComparison.Ordinal);

        // ...and only now does this bite.
        Assert.DoesNotContain(ApiKey, rendered, StringComparison.OrdinalIgnoreCase);

        // The address is still rendered in full: it is deliberately not a credential, and printing it
        // is what makes the override useful for diagnostics rather than merely silent.
        Assert.Contains(BaseUrl, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The direct call. A positional record's synthesised <c>ToString</c> renders every member, so
    /// without the override this type would print its own key — and it is handed to code about to
    /// make a network request, which is the code most likely to reach a log line. Neither existing
    /// layer covers that shape: both the <c>IHttpClientFactory</c> URI redaction and
    /// <c>LogMessageCleanser</c> are scoped to query strings, while a bare <c>ApiKey = value</c>
    /// inside a record's string form is not a URI at all (CLAUDE.md §1).
    /// </summary>
    [Fact]
    public async Task The_credential_redacts_its_key_when_rendered_as_a_string()
    {
        await using var context = CreateContext();
        var credential = await PlantAndReadCredentialAsync(context);

        AssertRedactedRendering(credential.ToString());
    }

    /// <summary>
    /// String interpolation, which is the shape a log call actually takes. Asserted separately from
    /// the direct call because one shape passing proves nothing about another — a reader should not
    /// have to reason about whether the interpolation path differs. It does not, and this pins it.
    /// </summary>
    [Fact]
    public async Task The_redaction_holds_when_the_credential_is_interpolated()
    {
        await using var context = CreateContext();
        var credential = await PlantAndReadCredentialAsync(context);

        AssertRedactedRendering($"probing {credential}");
    }

    /// <summary>
    /// <c>string.Format</c> and <c>Convert.ToString</c> — the two framework paths that format an
    /// object without the caller writing an interpolation. <c>Convert.ToString</c> is the one worth
    /// having: it takes a different route (via <c>IConvertible</c>/<c>IFormattable</c>) than the
    /// compiler's interpolation lowering, so it is the shape most likely to differ.
    /// </summary>
    [Fact]
    public async Task The_redaction_holds_through_string_Format_and_Convert_ToString()
    {
        await using var context = CreateContext();
        var credential = await PlantAndReadCredentialAsync(context);

        AssertRedactedRendering(string.Format("{0}", credential));
        AssertRedactedRendering(Convert.ToString(credential)!);
    }

    /// <summary>
    /// A <c>with</c> expression still redacts. The synthesised copy constructor does not regenerate
    /// <c>ToString</c>, so this holds — but asserting it is cheaper than requiring the next reader to
    /// reason about it, and it is the shape a "clone with a different address" helper would produce.
    /// </summary>
    [Fact]
    public async Task The_redaction_survives_a_with_expression()
    {
        await using var context = CreateContext();
        var credential = await PlantAndReadCredentialAsync(context);

        const string OtherBaseUrl = "http://sonarr-moved.example:8989/";
        var repointed = credential with { BaseUrl = new Uri(OtherBaseUrl) };

        var rendered = repointed.ToString();

        // POSITIVE CONTROL first, as above.
        Assert.Contains(CredentialPatterns.Replacement, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, rendered, StringComparison.OrdinalIgnoreCase);

        // The COPY's address is what is rendered, proving the with-expression produced a real new
        // value rather than this assertion passing against the original.
        Assert.Contains(OtherBaseUrl, rendered, StringComparison.Ordinal);
        Assert.Equal(ApiKey, repointed.ApiKey);
    }

    /// <summary>
    /// <b>Overriding <c>ToString</c> must not change VALUE SEMANTICS.</b> The cheapest way to break
    /// this bead's fix in a later cleanup is to convert the record to a class — which would compile,
    /// keep every redaction test above green, and silently turn equality into reference equality.
    ///
    /// <para>The INEQUALITY case is the one that bites: an <c>Equals</c> that ignored <c>ApiKey</c>
    /// would pass a same-key test perfectly, so two credentials differing only in their key must be
    /// asserted unequal.</para>
    /// </summary>
    [Fact]
    public async Task The_override_does_not_disturb_record_equality()
    {
        await using var context = CreateContext();
        var credential = await PlantAndReadCredentialAsync(context);

        var sameValues = new SonarrCredential(new Uri(BaseUrl), ApiKey);
        Assert.Equal(credential, sameValues);
        Assert.Equal(credential.GetHashCode(), sameValues.GetHashCode());

        // Differing ONLY in the key: still unequal. An Equals that ignored ApiKey would pass the
        // assertions above and fail only here.
        var differentKey = credential with { ApiKey = ApiKey + "-different" };
        Assert.NotEqual(credential, differentKey);

        // And differing only in the address, so neither member is being ignored.
        var differentAddress = credential with { BaseUrl = new Uri("http://sonarr-other.example:8989/") };
        Assert.NotEqual(credential, differentAddress);
    }

    /// <summary>
    /// <b>THE CONTROL THAT PROVES THE HAZARD IS REAL</b> (CLAUDE.md §4, and the reason this file's
    /// other assertions test something).
    ///
    /// <para>Without it, "SonarrCredential's ToString does not contain the key" is equally true of a
    /// language in which records never printed their members at all. This drives the SAME positional
    /// shape with NO override through the same planted key and asserts the default rendering DOES
    /// contain it — so the redaction above is demonstrably a redaction rather than an absence that
    /// was never at risk.</para>
    /// </summary>
    [Fact]
    public void The_default_record_ToString_does_print_the_key_which_is_why_the_override_exists()
    {
        var unprotected = new UnprotectedCredentialShapeControl(new Uri(BaseUrl), ApiKey);

        var rendered = unprotected.ToString();

        // The leak, demonstrated. If this ever stops being true, the override is no longer defending
        // against anything and this whole file needs rereading rather than deleting.
        Assert.Contains(ApiKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(CredentialPatterns.Replacement, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A DELIBERATE LEAK, AND IT MUST NEVER BE USED ON A PRODUCTION PATH.</b> This exists only to
    /// demonstrate what a positional record's DEFAULT <c>ToString</c> does with a secret member, which
    /// is the fact <see cref="The_default_record_ToString_does_print_the_key_which_is_why_the_override_exists"/>
    /// needs in order to prove the rest of this file is not vacuous.
    ///
    /// <para>It is NOT a stray duplicate of <see cref="SonarrCredential"/> and must not be tidied
    /// away as one: deleting it removes the only evidence that the redaction assertions are testing
    /// a real hazard. It is private to this test class so nothing outside can reach it.</para>
    /// </summary>
    private sealed record UnprotectedCredentialShapeControl(Uri BaseUrl, string ApiKey);
}
