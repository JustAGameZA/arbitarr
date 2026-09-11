using System.Net;
using Arbitarr.Core.Ai;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Tests;
using Arbitarr.Data.Logging;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-6vf: the cross-sink guarantee — BOTH credential sinks redact the same shapes, because both
/// now run the same <see cref="CredentialPatterns"/> implementation.
///
/// <para><b>Why this test lives here and not in Core.Tests.</b> It has to reference both
/// <see cref="LogMessageCleanser"/> (Arbitarr.Data) and <see cref="SanitizedErrorDescription"/>
/// (Arbitarr.Core). Data.Tests references Core, so this is the only project where both are
/// visible — Core.Tests cannot see the cleanser at all, which is the same layering constraint that
/// forced the duplication this bead removes.</para>
///
/// <para><b>What it is actually guarding.</b> Before arb-6vf the two sinks held separate copies
/// kept in step by a comment, and they HAD drifted: the space-separated prose form
/// ("invalid key sk-…") was added to the status scrubber under arb-fbx and never reached the log
/// cleanser, so that credential was redacted from the public dashboard while being written verbatim
/// into the log database. This test is what makes a future drift fail rather than go unnoticed.</para>
///
/// <para>It shares <see cref="CredentialPatternsTests.CredentialCorpus"/> rather than restating the
/// rows: two corpora kept equal by hand would reintroduce the exact failure mode being fixed.</para>
/// </summary>
public class CredentialPatternsCrossSinkTests
{
    public static TheoryData<string, string> CredentialCorpus() => CredentialPatternsTests.CredentialCorpus();

    /// <summary>
    /// Drives the scrubber through its PUBLIC surface, the way <c>GET /api/status</c> does.
    /// <c>ScrubForPublication</c> is internal and there is no <c>InternalsVisibleTo</c>, so calling
    /// it directly is not an option — and driving <see cref="SanitizedErrorDescription.Describe"/>
    /// is the stronger test anyway: it proves the redaction happens on the path that actually
    /// reaches an unauthenticated caller, not merely inside a helper something might stop calling.
    /// </summary>
    private static string Publish(string body) =>
        SanitizedErrorDescription.Describe(new OllamaRequestException(HttpStatusCode.BadRequest, body));

    /// <summary>
    /// <b>THE POINT OF THE BEAD.</b> Every shape in the shared corpus is redacted by the log
    /// cleanser AND by the status scrubber, each with its own positive control.
    /// </summary>
    [Theory]
    [MemberData(nameof(CredentialCorpus))]
    public void Both_sinks_redact_every_shared_credential_shape(string input, string secret)
    {
        // POSITIVE CONTROL: the secret is genuinely in the input, so neither absence assertion
        // below can pass vacuously (CLAUDE.md §4).
        Assert.Contains(secret, input, StringComparison.Ordinal);

        var cleansed = LogMessageCleanser.Cleanse(input);
        var scrubbed = Publish(input);

        // Detectability on each sink independently: the redaction token proves the pattern fired
        // in THAT sink, so a sink that silently stopped scrubbing fails here rather than passing
        // on the strength of the other one.
        Assert.Contains(LogMessageCleanser.Replacement, cleansed!, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, cleansed!, StringComparison.Ordinal);

        Assert.Contains(SanitizedErrorDescription.Replacement, scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two sinks agree on the replacement token. A divergence here would mean an operator
    /// reading a log and a dashboard sees two different vocabularies for the same event, and it is
    /// the cheapest possible detector for the consts having been un-aliased.
    /// </summary>
    [Fact]
    public void Both_sinks_use_the_one_shared_replacement_token()
    {
        Assert.Equal(CredentialPatterns.Replacement, LogMessageCleanser.Replacement);
        Assert.Equal(CredentialPatterns.Replacement, SanitizedErrorDescription.Replacement);
    }

    /// <summary>
    /// The asymmetry that must SURVIVE: only the status scrubber strips hosts. The log store behind
    /// <c>/api/admin/logs</c> is admin-gated and an operator needs the hostname that failed, so
    /// sharing the host arms would cost the diagnosis without protecting anyone unauthenticated.
    ///
    /// <para>Positive control both ways: the same input is shown to KEEP the host in one sink and
    /// LOSE it in the other, so this cannot pass by the host never having been present.</para>
    /// </summary>
    [Fact]
    public void Only_the_status_scrubber_removes_hosts()
    {
        const string input = "dial tcp ollama.internal.example:11434: connection refused";

        var cleansed = LogMessageCleanser.Cleanse(input);
        var scrubbed = Publish(input);

        // The cleanser keeps it — that is the diagnostic value the admin-gated log exists for.
        Assert.Contains("ollama.internal.example", cleansed!, StringComparison.Ordinal);
        // The unauthenticated surface does not.
        Assert.DoesNotContain("ollama.internal.example", scrubbed, StringComparison.Ordinal);
        Assert.Contains(SanitizedErrorDescription.Replacement, scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The webhook arm stays cleanser-only and is NOT shared: its credential sits in a URL path that
    /// the status scrubber never sees. Pins that consolidating the credential arms did not quietly
    /// move this one too.
    /// </summary>
    [Fact]
    public void The_webhook_arm_remains_cleanser_only()
    {
        const string secret = "PLACEHOLDERWEBHOOKTOKEN";
        var input = $"POST https://discord.com/api/webhooks/123456789/{secret} failed";

        Assert.Contains(secret, input, StringComparison.Ordinal);

        var cleansed = LogMessageCleanser.Cleanse(input);

        Assert.Contains(LogMessageCleanser.Replacement, cleansed!, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, cleansed!, StringComparison.Ordinal);
    }
}
