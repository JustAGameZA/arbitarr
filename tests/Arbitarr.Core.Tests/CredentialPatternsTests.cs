using Arbitarr.Core.Diagnostics;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-6vf: the shared credential redaction, tested as its own unit.
///
/// <para><b>Every row carries its positive control</b> (CLAUDE.md §4). An
/// <c>Assert.DoesNotContain(secret, output)</c> passes just as happily when the secret was never in
/// play, so each case first asserts the planted value IS in the input, then that
/// <see cref="CredentialPatterns.Replacement"/> IS in the output — which proves the pattern fired
/// and the value was removed, rather than the value simply never having arrived.</para>
///
/// <para>The cross-sink check — that both <c>LogMessageCleanser</c> and
/// <see cref="SanitizedErrorDescription"/> produce the same redaction from the same corpus — lives
/// in <c>Arbitarr.Data.Tests</c>, because that is the only test project that can reference both.
/// This file pins the shared implementation itself.</para>
///
/// <para>All fixture values are placeholders (PUBLIC repo): no real key, host or address.</para>
/// </summary>
public class CredentialPatternsTests
{
    /// <summary>
    /// The shared corpus. Each row is (input, the planted secret that must not survive). Kept as
    /// one collection so the cross-sink test in Data.Tests can assert the SAME shapes without the
    /// two lists drifting — which is the whole failure mode arb-6vf exists to remove.
    /// </summary>
    public static TheoryData<string, string> CredentialCorpus() => new()
    {
        // Query parameter, both spellings of the name.
        { "GET /api/search?apikey=PLACEHOLDERKEY123456 failed", "PLACEHOLDERKEY123456" },
        { "GET /api/search?api_key=PLACEHOLDERKEY123456 failed", "PLACEHOLDERKEY123456" },
        { "callback?token=PLACEHOLDERTOKEN9876 rejected", "PLACEHOLDERTOKEN9876" },
        { "grab?passkey=PLACEHOLDERPASS5544 denied", "PLACEHOLDERPASS5544" },
        // Authorization schemes.
        { "Authorization: Bearer PLACEHOLDERBEARER0011 was refused", "PLACEHOLDERBEARER0011" },
        { "Authorization: Basic UExBQ0VIT0xERVI6UEFTUw== was refused", "UExBQ0VIT0xERVI6UEFTUw==" },
        // Named credential, separator forms.
        { "X-Admin-Api-Key: PLACEHOLDERADMIN77 rejected", "PLACEHOLDERADMIN77" },
        { "{\"password\": \"PLACEHOLDERPW88\"}", "PLACEHOLDERPW88" },
        { "apikey=PLACEHOLDERINLINE99 in config", "PLACEHOLDERINLINE99" },
        { "client_secret=PLACEHOLDERSECRET12 expired", "PLACEHOLDERSECRET12" },
        // Space-separated prose form. This arm existed ONLY in the status scrubber before arb-6vf;
        // it is in the shared corpus now precisely because the log cleanser was missing it.
        { "invalid key PLACEHOLDERSPACED3344 supplied", "PLACEHOLDERSPACED3344" },
        { "rejected token PLACEHOLDERSPACED5566 at gateway", "PLACEHOLDERSPACED5566" },
    };

    [Theory]
    [MemberData(nameof(CredentialCorpus))]
    public void Every_credential_shape_is_redacted(string input, string secret)
    {
        // POSITIVE CONTROL: the planted value really is in the text being scrubbed, so the absence
        // assertion below is about the pattern working rather than about an empty set.
        Assert.Contains(secret, input, StringComparison.Ordinal);

        var redacted = CredentialPatterns.RedactCredentials(input);

        // Detectability: the pattern fired. Without this, deleting every pattern would still pass
        // the DoesNotContain below for any input that happened not to contain the secret.
        Assert.Contains(CredentialPatterns.Replacement, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The NAME survives while the value does not. "apikey=&lt;redacted&gt;" tells an operator which
    /// credential was present; bare "&lt;redacted&gt;" would leave them unable to tell an api key
    /// from a password. This pins the prefix-preserving replace, which a naive
    /// <c>Replace(text, Replacement)</c> would silently drop.
    /// </summary>
    [Fact]
    public void The_credential_name_survives_and_only_the_value_is_removed()
    {
        var redacted = CredentialPatterns.RedactCredentials("X-Admin-Api-Key: PLACEHOLDERADMIN77 rejected");

        Assert.Contains("X-Admin-Api-Key", redacted, StringComparison.Ordinal);
        Assert.Contains(CredentialPatterns.Replacement, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("PLACEHOLDERADMIN77", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// NEGATIVE THEORY — the over-redaction probe. A cleanser that redacted everything would pass
    /// every row above; these are the ordinary strings both sinks must stay readable for. A log line
    /// or a dashboard message redacted into uselessness is its own outage.
    /// </summary>
    [Theory]
    [InlineData("token expired")]                       // prose: below the 12-char value floor
    [InlineData("HTTP 400 Bad Request")]
    [InlineData("model llama3.1:8b not found")]
    [InlineData("request took 12:34:56 789 ms")]
    [InlineData("commit 9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c3b2a1f09")]
    [InlineData("release guid 0b5f2c1e-7a3d-4f10-9c8b-2e6a4d5f1b30")]
    public void Ordinary_text_is_left_intact(string input)
    {
        Assert.Equal(input, CredentialPatterns.RedactCredentials(input));
    }

    /// <summary>
    /// Idempotent: scrubbing already-scrubbed text changes nothing. The status scrubber runs over a
    /// value <c>OllamaRequestException</c> already scrubbed at construction, so this property is
    /// relied on rather than merely nice — and it also proves the replacement token itself cannot be
    /// re-captured as a credential value.
    /// </summary>
    [Theory]
    [MemberData(nameof(CredentialCorpus))]
    public void Redaction_is_idempotent(string input, string secret)
    {
        Assert.Contains(secret, input, StringComparison.Ordinal);

        var once = CredentialPatterns.RedactCredentials(input);
        var twice = CredentialPatterns.RedactCredentials(once);

        Assert.Equal(once, twice);
    }
}
