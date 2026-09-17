using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Security;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Logging;
using Arbitarr.Data.Media;
using Arbitarr.Data.Security;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-1ox9: a <see cref="SonarrCredential"/> carrying a planted key is driven through the REAL
/// logging stack, and the key reaches no row of the persistent log store.
/// </summary>
/// <remarks>
/// <para><b>WHY THIS EXISTS ALONGSIDE THE UNIT TEST.</b> <c>SonarrCredentialProviderTests</c> proves
/// the FORMATTER is correct. It does not prove the logging stack routes through it — and a correct
/// formatter that nothing calls is precisely the failure mode a unit test cannot see, which is the
/// stated purpose of <see cref="LogSecretInjectionTests"/>' second test. This drives the record
/// through an <c>ILogger</c> obtained from the app's own <c>ILoggerFactory</c> and reads the rows
/// back out of <see cref="LogStore"/>.</para>
///
/// <para><b>WHAT THE EXISTING LAYERS ACTUALLY COVER — measured, and not what this bead assumed.</b>
/// The <c>IHttpClientFactory</c> URI redaction collapses an outbound request URI's QUERY STRING and
/// nothing else, so it covers no record rendering at all. <see cref="LogMessageCleanser"/> is the
/// surprise: its <c>NamedCredential</c> arm matches a credential-shaped NAME followed by <c>:</c> or
/// <c>=</c> ANYWHERE in the text, not only inside a URI — so a rendered <c>ApiKey = …</c> IS scrubbed
/// by the sink today, contrary to the premise arb-1ox9 was filed on.</para>
///
/// <para>That does not make the overrides redundant, for two reasons this file pins rather than
/// argues. First, the coverage is INCOMPLETE, and it is incomplete by SHAPE rather than only by
/// vocabulary. <c>PlaintextKey = …</c> used to be the worked example — it matched no arm, so the two
/// types carrying a live admin credential were genuinely uncovered — until arb-cia3 taught the arm
/// <c>plaintext…</c>; that vocabulary gap is closed, but the structural one is not, because every one
/// of the four SHARED <c>CredentialPatterns</c> arms needs a credential-shaped NAME or SCHEME adjacent
/// to the value and a bare URL PATH SEGMENT has neither. The cleanser's own fifth arm,
/// <c>WebhookUrl</c>, is the sole exception and closes nothing here: it is HOST-scoped to Discord and
/// Telegram webhook URLs, so it does not reach the <c>.invalid</c> host the control below uses. See
/// <see cref="The_cleanser_does_not_cover_a_path_segment_secret_which_is_why_the_overrides_carry_the_weight"/>,
/// which asserts that leak as a fact about this pipeline, alongside the now-covered
/// <c>PlaintextKey</c> shape as its positive control. Second, the cleanser only runs in the LOG
/// SINK: a credential interpolated into an exception message, a console line, or any other
/// non-sink surface never reaches it, while <c>ToString</c> is where every one of those shapes is
/// formed. Redacting at the source is the layer that covers all of them.</para>
///
/// <para>Each shape asserts BOTH halves: the marker IS present (so the record really was formatted
/// into the row, rather than the row being empty) and the key is absent (CLAUDE.md §4).</para>
/// </remarks>
public sealed class CredentialRecordLogInjectionTests : IAsyncLifetime
{
    /// <summary>
    /// Follows the "secret-api-key" convention the pre-commit secret guard allowlists, with a
    /// distinctive suffix so a log-row search finds exactly this value.
    ///
    /// <para>Deliberately NOT in a query-string shape anywhere in this file: if it were, a pass could
    /// be explained by <see cref="LogMessageCleanser"/> rather than by the override under test.</para>
    /// </summary>
    private const string ApiKey = "secret-api-key-credential-record-1ox9";

    private static readonly Uri BaseUrl = new("http://sonarr.example:8989/");

    /// <summary>
    /// A host over a FRESH, per-instance config directory, via the parameterless constructor the
    /// factory documents as "what every test that does not explicitly need otherwise should use".
    ///
    /// <para><b>Deliberately NOT <c>OverConfigDirectory</c>, and that is a bug fix rather than a
    /// style choice.</b> This class first copied <see cref="LogSecretInjectionTests"/>'
    /// caller-supplied-directory shape, which exists for the arb-v3w case of building a SECOND host
    /// over the same database file. Nothing here needs that, and taking it made this class own a
    /// teardown it did not need: xUnit constructs the class once PER TEST, so the seven tests stood
    /// up seven hosts whose disposal raced the directory delete, and the suite began failing
    /// intermittently with SQLite "database is locked" — in OTHER classes' tests, which is what made
    /// it look like an ambient flake. Verified by isolation: the assembly was green four times over
    /// on master and with this file removed, and red with it present. The parameterless constructor
    /// gives each instance its own directory and owns the drain-then-delete itself.</para>
    /// </summary>
    private readonly ArbitarrWebApplicationFactory _root = new();

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Disposes the host this class owns. The factory's own <c>Dispose</c> drains the host before
    /// deleting its config directory — which holds the LOG database these assertions read, so the
    /// drain has to cover that second database too (arb-gphi).
    /// </summary>
    public async Task DisposeAsync() => await _root.DisposeAsync();

    /// <summary>
    /// The structured-argument shape: <c>logger.LogWarning("… {Credential}", credential)</c>. The
    /// message formatter calls <c>ToString</c> on the argument, so this is the commonest way a
    /// credential would reach a row.
    /// </summary>
    [Fact]
    public async Task A_credential_logged_as_a_structured_argument_reaches_no_row_carrying_its_key()
    {
        await AssertCredentialIsRedactedInTheLogAsync(
            "StructuredArgument",
            (logger, credential) => logger.LogWarning("credential was {Credential}", credential));
    }

    /// <summary>
    /// Interpolated BEFORE the call, so the logging stack never sees the record at all — only the
    /// already-rendered string. Asserted separately because it exercises the compiler's interpolation
    /// lowering rather than the logging formatter, and a pass on one says nothing about the other.
    /// </summary>
    [Fact]
    public async Task A_credential_interpolated_into_the_message_reaches_no_row_carrying_its_key()
    {
        await AssertCredentialIsRedactedInTheLogAsync(
            "Interpolated",
#pragma warning disable CA2254 // Deliberately a non-constant template: that IS the shape under test.
            (logger, credential) => logger.LogWarning($"credential was {credential}"));
#pragma warning restore CA2254
    }

    /// <summary>
    /// <b>THE DESTRUCTURING SHAPE, AND THE ONE MOST LIKELY TO BYPASS THE OVERRIDE.</b> The <c>@</c>
    /// prefix asks the logging provider to serialise the object's MEMBERS rather than call
    /// <c>ToString</c> — which for a record would print the key the override exists to hide.
    ///
    /// <para><b>What this stack actually does, established by running it rather than assumed.</b>
    /// Serilog is not in the dependency graph (no package reference anywhere; the only occurrences of
    /// the name in <c>src/</c> are comments in <c>LogStore</c> about accepting Serilog's LEVEL
    /// spellings as aliases). Arbitarr logs through <c>Microsoft.Extensions.Logging</c>, whose default
    /// <c>LogValuesFormatter</c> IGNORES the <c>@</c> prefix and formats the argument with
    /// <c>ToString</c>. So the override does cover this shape today.</para>
    ///
    /// <para><b>The test is worth having anyway, and this is why:</b> if Serilog (or any destructuring
    /// provider) is ever adopted, <c>@</c> starts serialising members and this test goes RED rather
    /// than the key starting to leak silently. That is the entire value — it pins a behaviour the
    /// stack currently gives for free, so the day it stops being free is a build failure and not an
    /// incident.</para>
    /// </summary>
    [Fact]
    public async Task A_credential_logged_with_the_destructuring_prefix_reaches_no_row_carrying_its_key()
    {
        await AssertCredentialIsRedactedInTheLogAsync(
            "Destructured",
            (logger, credential) => logger.LogWarning("credential was {@Credential}", credential));
    }

    /// <summary>
    /// <b>THE SAME THREE SHAPES ON THE RECORD THE CLEANSER CANNOT SAVE.</b> This is the test that
    /// actually pins <c>ToString</c>, and the reason it exists is a mutation result rather than a
    /// hunch.
    ///
    /// <para>Deleting <c>SonarrCredential.ToString</c> leaves every <c>SonarrCredential</c> assertion
    /// in this file GREEN, because the sink's <c>NamedCredential</c> arm scrubs the rendered
    /// <c>ApiKey = …</c> on the way in. Those tests are therefore a genuine end-to-end check that the
    /// pipeline does not leak — worth keeping — but they do not attribute the redaction to the
    /// override, and taken alone they would let the override be deleted silently.</para>
    ///
    /// <para><b>arb-cia3 changed what this test attributes, and the honest statement is narrower
    /// than the one it replaced.</b> It used to read: <c>CreatedApiKey</c> renders
    /// <c>PlaintextKey = …</c>, which matches NO arm of the cleanser, so THIS test goes red the
    /// moment the override is removed. The first clause is no longer true — arb-cia3 taught
    /// <c>NamedCredential</c> the <c>plaintext…</c> alternative precisely because that shape was
    /// reaching the store verbatim — and therefore the second clause is not true AT THIS SINK
    /// either: with the override deleted, the cleanser would now scrub the rendering on the way in
    /// and these rows would stay green. Left unedited, this paragraph would have been a comforting
    /// half-truth of exactly the kind the file's own remarks warn about.</para>
    ///
    /// <para>What the test still proves is the end-to-end fact: this pipeline does not leak the
    /// plaintext of a minted admin key in any of the three shapes. What now carries the ATTRIBUTION
    /// — that the override, not the sink, is the mechanism — is
    /// <c>CredentialRecordToStringTests</c> (the source-side sweep, arb-1ox9) together with
    /// <see cref="The_cleanser_does_not_cover_a_path_segment_secret_which_is_why_the_overrides_carry_the_weight"/>,
    /// whose control is a URL PATH SEGMENT: a structural gap no future widening of a name
    /// alternation can close, unlike the vocabulary gap arb-cia3 just closed under this one.</para>
    ///
    /// <para>All three shapes are driven, not just one: the structured argument, the pre-interpolated
    /// message, and the destructuring <c>@</c> prefix, for the same reasons they are asserted
    /// separately for <c>SonarrCredential</c>.</para>
    /// </summary>
    [Theory]
    [InlineData("StructuredArgument")]
    [InlineData("Interpolated")]
    [InlineData("Destructured")]
    public async Task A_created_api_key_reaches_no_row_carrying_its_plaintext(string shapeName)
    {
        var loggerName = $"Arbitarr.Test.CreatedApiKey.{shapeName}";

        using var client = _root.CreateClient();
        _ = await client.GetAsync("/api/health");

        var store = _root.Services.GetRequiredService<LogStore>();
        var logger = _root.Services.GetRequiredService<ILoggerFactory>().CreateLogger(loggerName);

        var created = new CreatedApiKey(
            new ApiKeyEntry
            {
                Id = 4242,
                Label = "credential-record-probe",
                Scope = ApiKeyScope.Admin,

                // Not a real hash and never verified against: this row is never persisted, it is
                // only the non-secret half of the record being rendered.
                KeyHash = "not-a-real-hash",
            },
            ApiKey);

        // The key is in play: a formatter printing members verbatim would have it to print.
        Assert.Equal(ApiKey, created.PlaintextKey);

        switch (shapeName)
        {
            case "StructuredArgument":
                logger.LogWarning("created {Created}", created);
                break;
            case "Interpolated":
#pragma warning disable CA2254 // Deliberately a non-constant template: that IS the shape under test.
                logger.LogWarning($"created {created}");
#pragma warning restore CA2254
                break;
            default:
                logger.LogWarning("created {@Created}", created);
                break;
        }

        await _root.Services.FlushLogSinkAsync();

        var entries = await ReadEntriesForLoggerAsync(store, loggerName);

        // The row exists before any absence is asserted.
        Assert.NotEmpty(entries);

        foreach (var entry in entries)
        {
            // POSITIVE CONTROL: the record really was formatted into the row. The row's id is the
            // non-secret member the override renders, so its presence proves the override's output
            // is what landed — not an empty row, and not some other rendering.
            Assert.Contains("4242", entry.Message, StringComparison.Ordinal);

            // ...therefore these absences are the override's doing. Nothing else in the pipeline
            // covers the PlaintextKey spelling.
            Assert.DoesNotContain(ApiKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiKey, entry.Logger, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <b>THE CLEANSER'S COVERAGE OF RECORD RENDERINGS IS PARTIAL AND INCIDENTAL, AND THIS PINS
    /// WHERE IT STOPS.</b> Measured, not assumed — and it did not match what this bead was filed
    /// believing.
    ///
    /// <para>The premise arb-1ox9 started from was that <see cref="LogMessageCleanser"/> is scoped to
    /// query strings, so no record rendering is covered. That is NOT what the code does.
    /// <c>CredentialPatterns</c>' <c>NamedCredential</c> arm matches a credential-shaped NAME
    /// followed by <c>:</c> or <c>=</c> ANYWHERE in the text, not only in a URI — so
    /// <c>ApiKey = …</c> and <c>PlaintextToken = …</c> ARE scrubbed by the sink, because
    /// "apikey" and "token" appear in that arm's alternation.</para>
    ///
    /// <para><b>Where it stops is no longer <c>PlaintextKey</c> (arb-cia3).</b> When this test was
    /// written the alternation was <c>api_key|apikey|token|passkey|password|secret</c>, "plaintextkey"
    /// contained none of them, and <c>CreatedApiKey</c> / <c>CreatedApiKeyResponse</c> — the two types
    /// carrying a live ADMIN credential — had a rendering NOTHING in the pipeline covered. arb-cia3
    /// taught the arm <c>plaintext…</c>, so that specific shape IS now scrubbed by the sink. This test
    /// took the RED its own closing paragraph predicted, and was rewritten rather than deleted,
    /// exactly as that paragraph instructed.</para>
    ///
    /// <para><b>Why this test still asserts a leak rather than a redaction.</b> Every other assertion
    /// in this file would look identical if the cleanser were quietly doing the work, which would make
    /// the overrides decorative. The attribution argument therefore needs SOME shape the cleanser
    /// demonstrably does not cover, driven through the same sink with no override in play, landing
    /// VERBATIM. A secret in a URL PATH SEGMENT is that shape and is a structural gap rather than a
    /// vocabulary one: every one of the four SHARED <c>CredentialPatterns</c> arms requires a
    /// credential-shaped NAME or SCHEME adjacent to the value, and a bare path segment has neither.
    /// The cleanser's own fifth arm, <c>WebhookUrl</c>, DOES match a bare path segment without any
    /// such name — but only behind a HOST-scoped prefix (Discord and Telegram webhook URLs), so it
    /// does not reach the <c>.invalid</c> host below — which is the same gap CLAUDE.md §1 and
    /// <c>docs/standards/architecture.md</c> point at when they say such registrations need
    /// <c>.RemoveAllLoggers()</c>. Widening a name alternation can never close it, so this control
    /// cannot be invalidated by the next word added to the list.</para>
    ///
    /// <para>The <c>PlaintextKey</c> half is kept below as a POSITIVE control on arb-cia3's arm: the
    /// same sink, the same raw text, now redacted. Keeping both in one test is what makes the two
    /// assertions comparable — same host, same flush, same reader — so "the sink scrubs this but not
    /// that" is measured rather than inferred from two separate runs.</para>
    ///
    /// <para>If the path-segment assertion ever goes RED, the cleanser has gained structural coverage
    /// of URL paths. That is not a regression, but this file's attribution argument, CLAUDE.md §1 and
    /// architecture.md's logging section all need rereading together rather than the test being
    /// deleted.</para>
    /// </summary>
    [Fact]
    public async Task The_cleanser_does_not_cover_a_path_segment_secret_which_is_why_the_overrides_carry_the_weight()
    {
        const string LoggerName = "Arbitarr.Test.CredentialRecord.CleanserControl";

        // One real request first: this is what starts the host and wires the SQLite sink, so a
        // logger obtained before it would write nowhere and every assertion below would fail on an
        // empty table for a reason that has nothing to do with the redaction. LogSecretInjectionTests
        // does the same, for the same reason.
        using var client = _root.CreateClient();
        _ = await client.GetAsync("/api/health");

        var store = _root.Services.GetRequiredService<LogStore>();
        var logger = _root.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerName);

        // THE CONTROL: the same key as a bare URL PATH SEGMENT. No credential-shaped name or scheme
        // sits next to it, so no arm of the cleanser can reach it. No record is involved — this is
        // raw text, so the only thing that could redact it is the sink.
        logger.LogWarning(
            "probe failed for https://media.example.invalid/feed/{0}/rss", ApiKey);

        // POSITIVE CONTROL on arb-cia3's arm, through the SAME sink: exactly what CreatedApiKey's
        // SYNTHESISED ToString would have produced. Before arb-cia3 this landed verbatim; it is here
        // so the contrast below is measured against a shape the sink demonstrably DOES cover, rather
        // than the leak assertion passing because the sink stopped working altogether.
        logger.LogWarning(
            "created CreatedApiKey {{ Entry = ..., PlaintextKey = {0} }}", ApiKey);

        await _root.Services.FlushLogSinkAsync();

        var entries = await ReadEntriesForLoggerAsync(store, LoggerName);
        Assert.NotEmpty(entries);

        // The path-segment key survives the sink untouched: no arm has a name or scheme to anchor on.
        var pathEntry = Assert.Single(entries, e => e.Message.Contains("/feed/", StringComparison.Ordinal));
        Assert.Contains(ApiKey, pathEntry.Message, StringComparison.Ordinal);

        // The PlaintextKey rendering does NOT — arb-cia3's arm scrubs it, and the replacement marker
        // proves the arm fired rather than the text never having arrived (CLAUDE.md §4).
        var recordEntry = Assert.Single(entries, e => e.Message.Contains("PlaintextKey", StringComparison.Ordinal));
        Assert.Contains(LogMessageCleanser.Replacement, recordEntry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, recordEntry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Drives one logging shape and asserts both halves over EVERY field of EVERY matching entry.
    ///
    /// <para>Asserting over <c>Message</c>, <c>Exception</c> and <c>Logger</c> rather than only
    /// <c>Message</c>: the exception text is the field most likely to carry a rendered argument, and
    /// checking only the message lets the most probable leak through while looking thorough — the
    /// discipline <see cref="LogSecretInjectionTests"/> states.</para>
    /// </summary>
    private async Task AssertCredentialIsRedactedInTheLogAsync(
        string shapeName,
        Action<ILogger, SonarrCredential> log)
    {
        var loggerName = $"Arbitarr.Test.CredentialRecord.{shapeName}";

        // One real request first: this is what starts the host and wires the SQLite sink. A logger
        // obtained before it writes nowhere, and the NotEmpty assertion below would then fail for a
        // reason that has nothing to do with the redaction under test.
        using var client = _root.CreateClient();
        _ = await client.GetAsync("/api/health");

        var store = _root.Services.GetRequiredService<LogStore>();
        var logger = _root.Services.GetRequiredService<ILoggerFactory>().CreateLogger(loggerName);

        var credential = new SonarrCredential(BaseUrl, ApiKey);

        // The key is in play: a formatter printing members verbatim would have it to print.
        Assert.Equal(ApiKey, credential.ApiKey);

        log(logger, credential);

        await _root.Services.FlushLogSinkAsync();

        var entries = await ReadEntriesForLoggerAsync(store, loggerName);

        // The row exists before any absence is asserted — an empty table satisfies "no row contains
        // the key" exactly as happily as a correct redaction.
        Assert.NotEmpty(entries);

        foreach (var entry in entries)
        {
            var exception = entry.Exception ?? string.Empty;

            // POSITIVE CONTROL: the record really was formatted into the row and the redaction fired.
            Assert.Contains(CredentialPatterns.Replacement, entry.Message, StringComparison.Ordinal);

            // ...therefore these absences are real redactions rather than an empty or truncated row.
            Assert.DoesNotContain(ApiKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiKey, exception, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiKey, entry.Logger, StringComparison.OrdinalIgnoreCase);

            // The address survives: the override is useful for diagnostics, not merely silent.
            Assert.Contains(BaseUrl.ToString(), entry.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The rows this logger wrote.
    ///
    /// <para><b>Matched on the STRIPPED category.</b> <c>SqliteLoggerProvider</c> stores categories
    /// with the <c>Arbitarr.</c> prefix removed (it is on every Arbitarr logger, so keeping it would
    /// cost a column's width to say the same word on every row). A filter written against the name
    /// as PASSED to <c>CreateLogger</c> therefore matches nothing, and every assertion downstream
    /// fails on an empty collection for a reason unrelated to the redaction — which is exactly what
    /// happened while writing this file.</para>
    /// </summary>
    private static async Task<List<LogEntry>> ReadEntriesForLoggerAsync(LogStore store, string loggerName)
    {
        var storedName = loggerName.StartsWith(ArbitarrCategoryPrefix, StringComparison.Ordinal)
            ? loggerName[ArbitarrCategoryPrefix.Length..]
            : loggerName;

        var page = await store.ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);
        return page.Entries
            .Where(e => string.Equals(e.Logger, storedName, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>The prefix <c>SqliteLoggerProvider</c> strips from every stored category.</summary>
    private const string ArbitarrCategoryPrefix = "Arbitarr.";
}
