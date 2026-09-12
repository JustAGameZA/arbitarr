using Arbitarr.Core.Sources;
using Arbitarr.Data.Sources;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// The write boundary's half of the shared origin corpus (arb-4vzm, arb-iub9), plus the property
/// that binds the two halves together: for every base URL <see cref="SourceRepository.ValidateBaseUrl"/>
/// accepts, a link at that exact origin passes
/// <see cref="Arbitarr.Core.Sources.TorznabFeedParser.TryValidateOriginPinnedLink"/>.
///
/// <para>That agreement property is the most valuable assertion in this file. Each of the three
/// beads was an instance of the two boundaries answering "what is a legitimate origin?" differently;
/// asserting the answers agree over the whole corpus is what a FOURTH escape form would break,
/// without anyone having to have thought of it in advance.</para>
///
/// <para><c>OriginPinnedLinkTests</c> in <c>Arbitarr.Core.Tests</c> drives the same
/// <see cref="UpstreamOriginCorpus"/> at the pin.</para>
/// </summary>
public sealed class SourceBaseUrlOriginTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new("arbitarr-source-origin-test");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    [Theory]
    [MemberData(nameof(UpstreamOriginCorpus.BaseUrlCases), MemberType = typeof(UpstreamOriginCorpus))]
    public void ValidateBaseUrl_accepts_exactly_the_corpus_forms_it_should(string baseUrl, bool accepted, string because)
    {
        var exception = Record.Exception(() => SourceRepository.ValidateBaseUrl(baseUrl));

        if (accepted)
        {
            Assert.True(exception is null, $"'{baseUrl}' should have been accepted ({because}) but was refused: {exception?.Message}");
        }
        else
        {
            Assert.True(exception is SourceValidationException, $"'{baseUrl}' should have been refused ({because}) but was accepted");
        }
    }

    /// <summary>
    /// S7 — the agreement property, and the single most valuable assertion in this PR: every base
    /// URL the write boundary ACCEPTS must be an origin whose own indexer's links pass the pin. Each
    /// of the three beads was an instance of that failing, so asserting it over the whole corpus is
    /// what a FOURTH escape form would break without anyone having had to think of it.
    ///
    /// <para><b>The link is built at every spelling of the host DNS treats as the same name</b>, not
    /// only at the origin's own text — and that distinction is load-bearing, found by mutation.
    /// Composing the link with <c>new Uri(origin, "dl")</c> alone copies the origin's OWN host
    /// string, so a trailing-dot origin trivially matches itself and the property cannot see
    /// arb-iub9 at all: re-accepting the trailing dot left that weaker version green. The real
    /// failure is that the indexer answers under the canonical (dot-free) name while the stored
    /// origin carries the dot, so the two texts differ even though DNS resolves both to one host.</para>
    ///
    /// <para>Asserted in ONE direction on purpose. The mirror — a dot-free origin receiving dotted
    /// links — is not asserted, because it is not a property the write boundary can establish: the
    /// link side is upstream-supplied, so an indexer choosing to emit dotted links would fail the
    /// pin whatever was stored. What is in scope, and what this pins, is that no value the write
    /// boundary accepts can put the STORED origin on the losing side of that comparison.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(UpstreamOriginCorpus.AcceptedBaseUrls), MemberType = typeof(UpstreamOriginCorpus))]
    public void Every_accepted_base_url_is_an_origin_its_own_links_pass(string baseUrl)
    {
        SourceRepository.ValidateBaseUrl(baseUrl);

        var origin = new Uri(baseUrl, UriKind.Absolute);

        foreach (var link in DnsEquivalentLinks(origin))
        {
            Assert.True(
                TorznabFeedParser.TryValidateOriginPinnedLink(link, origin, out _),
                $"base URL '{baseUrl}' is accepted at the write boundary but a link its own indexer could legitimately return ('{link}') fails the pin — the two boundaries disagree");
        }
    }

    /// <summary>
    /// The link spellings an indexer at <paramref name="origin"/> could legitimately return: the
    /// origin's own host text, and — when the stored host carries a root dot — the canonical
    /// dot-free form DNS treats as the same name. See the S7 doc above for why only that direction.
    /// </summary>
    private static IEnumerable<string> DnsEquivalentLinks(Uri origin)
    {
        yield return new Uri(origin, "dl?id=1").AbsoluteUri;

        if (origin.HostNameType is UriHostNameType.Dns && origin.Host.EndsWith('.'))
        {
            yield return new UriBuilder(origin) { Host = origin.Host.TrimEnd('.'), Path = "/dl", Query = "id=1" }.Uri.AbsoluteUri;
        }
    }

    /// <summary>
    /// S1 — the write boundary refuses userinfo and persists nothing. Asserted through
    /// <see cref="SourceRepository.AddAsync"/> rather than against the validator alone, because the
    /// property that matters is that no ROW is written.
    /// </summary>
    [Fact]
    public async Task AddAsync_refuses_a_userinfo_base_url_and_persists_nothing()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.AddAsync(
            kind: SourceRepository.NzbHydraKind,
            displayName: "Credential Bearing",
            baseUrl: $"https://user:{UpstreamOriginCorpus.PlantedPassword}@indexer.example:9117",
            apiKey: null,
            enabled: true,
            CancellationToken.None));

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    /// <summary>S1 for the other write path — an update must refuse the same value.</summary>
    [Fact]
    public async Task UpdateAsync_refuses_a_userinfo_base_url()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: SourceRepository.NzbHydraKind,
            displayName: "Clean",
            baseUrl: "https://indexer.example:9117",
            apiKey: null,
            enabled: true,
            CancellationToken.None);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.UpdateAsync(
            source.Id,
            kind: SourceRepository.NzbHydraKind,
            displayName: "Clean",
            baseUrl: $"https://user:{UpstreamOriginCorpus.PlantedPassword}@indexer.example:9117",
            apiKey: null,
            enabled: true,
            CancellationToken.None));

        var stored = Assert.Single(await repository.GetAllAsync(CancellationToken.None));
        Assert.Equal("https://indexer.example:9117", stored.BaseUrl);
    }

    /// <summary>
    /// S2 — the userinfo rejection message does not echo the value, WITH the positive control
    /// CLAUDE.md §4 requires: the same planted password is first driven through an arm that DOES
    /// echo (<c>.invalid</c>), showing the assertion is capable of finding it. Without that control,
    /// <c>Assert.DoesNotContain</c> would pass just as happily against a message that never saw the
    /// value at all.
    /// </summary>
    [Fact]
    public void The_userinfo_rejection_does_not_echo_the_credential()
    {
        // Positive control: an echoing arm, same needle. If this does not fire, the assertion below
        // is vacuous and proves nothing.
        var echoing = Assert.Throws<SourceValidationException>(
            () => SourceRepository.ValidateBaseUrl($"https://indexer.{UpstreamOriginCorpus.PlantedPassword}.invalid:9117"));
        Assert.Contains(UpstreamOriginCorpus.PlantedPassword, echoing.Message, StringComparison.Ordinal);

        // The real assertion: the userinfo arm, which must NOT echo.
        var refusal = Assert.Throws<SourceValidationException>(
            () => SourceRepository.ValidateBaseUrl($"https://user:{UpstreamOriginCorpus.PlantedPassword}@indexer.example:9117"));
        Assert.DoesNotContain(UpstreamOriginCorpus.PlantedPassword, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("user", refusal.Message.Replace(UpstreamOrigin.UserInfoFault, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// The arm ORDER is load-bearing, and this is what pins it: a URL that is BOTH
    /// credential-bearing AND a <c>.invalid</c> host must be refused by the non-echoing userinfo arm.
    /// Re-sorting the arms so <c>.invalid</c> ran first would print the credential into the response
    /// body, and only this test would notice.
    /// </summary>
    [Fact]
    public void A_credential_bearing_dot_invalid_url_is_refused_without_echoing_the_credential()
    {
        var refusal = Assert.Throws<SourceValidationException>(
            () => SourceRepository.ValidateBaseUrl($"https://user:{UpstreamOriginCorpus.PlantedPassword}@indexer.example.invalid:9117"));

        Assert.DoesNotContain(UpstreamOriginCorpus.PlantedPassword, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(UpstreamOrigin.UserInfoFault, refusal.Message);
    }

    /// <summary>
    /// S6 — the trailing dot is refused, and the message NAMES the trailing dot so the operator can
    /// act. A bare "not a valid URL" here would be the diagnosability failure arb-iub9 is about,
    /// merely relocated from the search result to the error message.
    /// </summary>
    [Fact]
    public void The_trailing_dot_refusal_names_the_trailing_dot()
    {
        var refusal = Assert.Throws<SourceValidationException>(
            () => SourceRepository.ValidateBaseUrl("http://indexer.example.:9117"));

        Assert.Contains("trailing dot", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// S8 — the pre-existing arms still hold. Regression per arm, so a rewrite that dropped one is
    /// caught by name rather than by the corpus theory alone.
    /// </summary>
    [Theory]
    [InlineData("http://indexer.example.invalid:9117", ".invalid")]
    [InlineData("http://invalid:9117", "invalid")]
    public void The_dot_invalid_arm_still_refuses(string baseUrl, string expectedFragment)
    {
        var refusal = Assert.Throws<SourceValidationException>(() => SourceRepository.ValidateBaseUrl(baseUrl));
        Assert.Contains(expectedFragment, refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>S8 — a plain valid URL is still accepted, the positive control for all of the above.</summary>
    [Fact]
    public void A_plain_valid_base_url_is_still_accepted() =>
        Assert.Null(Record.Exception(() => SourceRepository.ValidateBaseUrl("https://indexer.example:9117")));

    /// <summary>
    /// The EXISTING-ROWS decision, pinned. The write boundary is new; a row stored before it keeps
    /// its value, and <see cref="SourceRepository.UpdateAsync"/> validates the INCOMING base URL, not
    /// the stored one — so the operator is NOT locked out of editing an affected row, provided they
    /// supply a clean URL with the edit. The row is also not silently repaired: nothing rewrites a
    /// stored value behind the operator's back.
    /// </summary>
    [Fact]
    public async Task A_row_stored_with_credentials_keeps_its_value_and_can_still_be_edited()
    {
        var legacyBaseUrl = $"https://user:{UpstreamOriginCorpus.PlantedPassword}@indexer.example:9117";

        using var context = CreateContext();
        // Written directly against the DbContext, exactly as a row predating the validator would be.
        context.Sources.Add(new Entities.Source
        {
            Kind = SourceRepository.NzbHydraKind,
            DisplayName = "Legacy",
            BaseUrl = legacyBaseUrl,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(CancellationToken.None);

        var repository = new SourceRepository(context);
        var stored = Assert.Single(await repository.GetAllAsync(CancellationToken.None));

        // NOT filtered on read: the operator sees the value they must act on. Filtering it in the
        // projection would be a second, quieter policy disagreeing with the validator, and a hidden
        // credential is worse than a visible one.
        Assert.Equal(legacyBaseUrl, stored.BaseUrl);

        // And the operator can repair it, because UpdateAsync validates what they SEND.
        var repaired = await repository.UpdateAsync(
            stored.Id,
            kind: SourceRepository.NzbHydraKind,
            displayName: "Legacy",
            baseUrl: "https://indexer.example:9117",
            apiKey: null,
            enabled: true,
            CancellationToken.None);

        Assert.Equal("https://indexer.example:9117", repaired.BaseUrl);
    }

    /// <summary>
    /// The existing-rows WARNING names the row id and NOTHING else — never the value, never the
    /// host, never the username. This line lands in the persistent log store served at
    /// <c>/api/admin/logs</c> (CLAUDE.md §1), where <c>LogMessageCleanser</c> scrubs query strings
    /// and would not touch a credential in a URL's userinfo component; logging the value in order to
    /// complain about it would perform the leak. Same constraint as
    /// <c>security-properties-x7w8.md</c>'s P7.
    ///
    /// <para>Asserted PER ROW over a three-row fixture: two affected, one clean. "Some row warned"
    /// would still pass against an implementation that warned about all three, including the clean
    /// one — which would be a false alarm the operator cannot act on.</para>
    /// </summary>
    [Fact]
    public async Task The_existing_row_warning_names_only_the_row_id()
    {
        var recorder = new RecordingLogger<SourceRepository>();

        using var context = CreateContext();
        var affectedOne = AddLegacyRow(context, "Affected One", $"https://alice:{UpstreamOriginCorpus.PlantedPassword}@indexer.example:9117");
        var clean = AddLegacyRow(context, "Clean", "https://indexer.example:9118");
        var affectedTwo = AddLegacyRow(context, "Affected Two", $"https://bob:{UpstreamOriginCorpus.PlantedPassword}@indexer.example:9119");
        await context.SaveChangesAsync(CancellationToken.None);

        var repository = new SourceRepository(context, recorder);
        await repository.GetAllAsync(CancellationToken.None);

        // Per row, by identity — not "two warnings were emitted".
        Assert.Contains(recorder.Messages, m => m.Contains($"Source {affectedOne.Id}'s", StringComparison.Ordinal));
        Assert.Contains(recorder.Messages, m => m.Contains($"Source {affectedTwo.Id}'s", StringComparison.Ordinal));
        Assert.DoesNotContain(recorder.Messages, m => m.Contains($"Source {clean.Id}'s", StringComparison.Ordinal));

        var everythingLogged = string.Join('\n', recorder.Messages);

        // POSITIVE CONTROL, first: show these assertions fire against a line that DOES carry the
        // material. Without it, the absences below pass against an empty log.
        var control = $"Source {affectedOne.Id}'s base URL is https://alice:{UpstreamOriginCorpus.PlantedPassword}@indexer.example:9117";
        Assert.Contains(UpstreamOriginCorpus.PlantedPassword, control, StringComparison.Ordinal);
        Assert.Contains("alice", control, StringComparison.Ordinal);
        Assert.Contains("indexer.example", control, StringComparison.Ordinal);

        // The real assertions, against what was actually logged.
        Assert.NotEmpty(recorder.Messages);
        Assert.DoesNotContain(UpstreamOriginCorpus.PlantedPassword, everythingLogged, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", everythingLogged, StringComparison.Ordinal);
        Assert.DoesNotContain("bob", everythingLogged, StringComparison.Ordinal);
        Assert.DoesNotContain("indexer.example", everythingLogged, StringComparison.Ordinal);
    }

    private static Entities.Source AddLegacyRow(ArbitarrDbContext context, string displayName, string baseUrl)
    {
        var row = new Entities.Source
        {
            Kind = SourceRepository.NzbHydraKind,
            DisplayName = displayName,
            BaseUrl = baseUrl,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        context.Sources.Add(row);
        return row;
    }

    /// <summary>
    /// Captures formatted log messages. Deliberately records the FORMATTED text rather than the
    /// structured state: the formatted text is what reaches the persistent log store, so it is what
    /// a leak assertion must be made against.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
