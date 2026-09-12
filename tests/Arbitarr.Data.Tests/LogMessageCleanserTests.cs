using System.Text;
using System.Text.RegularExpressions;
using Arbitarr.Data.Logging;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #65 plan §5: the cleanser redacts a URL-embedded key, an Authorization-style value, and a
/// webhook URL.
///
/// These assert the DEFENCE-IN-DEPTH layer, not the primary control. The guard that actually holds
/// the line is <c>LogSecretInjectionTests</c>, which proves a real key never enters a log row in the
/// first place — see <see cref="LogMessageCleanser"/>'s own remarks for why a denylist is the
/// second layer here rather than the strategy.
/// </summary>
public sealed class LogMessageCleanserTests
{
    private const string SecretValue = "secret-api-key-abcdef123456";

    /// <summary>
    /// arb-7j5x: a distinct value of the SAME shape as <see cref="SecretValue"/>, for the
    /// second-occurrence theory. Distinct so an assertion cannot pass on the first value's redaction.
    /// </summary>
    private const string SecondSecretValue = "secret-api-key-second99887";

    [Theory]
    [InlineData("GET http://192.0.2.10:5076/api?apikey=" + SecretValue + " failed")]
    [InlineData("GET http://192.0.2.10:5076/api?t=search&api_key=" + SecretValue + "&q=x failed")]
    [InlineData("http://192.0.2.10/rss?passkey=" + SecretValue)]
    [InlineData("http://192.0.2.10/x?token=" + SecretValue)]
    public void Redacts_a_credential_carried_in_a_query_string(string message)
    {
        var cleansed = LogMessageCleanser.Cleanse(message);

        Assert.DoesNotContain(SecretValue, cleansed);
        Assert.Contains(LogMessageCleanser.Replacement, cleansed);
    }

    [Fact]
    public void Redacts_an_authorization_bearer_value()
    {
        var cleansed = LogMessageCleanser.Cleanse($"Authorization: Bearer {SecretValue}");

        Assert.DoesNotContain(SecretValue, cleansed);
        Assert.Contains(LogMessageCleanser.Replacement, cleansed);
    }

    [Fact]
    public void Redacts_a_named_credential_header()
    {
        var cleansed = LogMessageCleanser.Cleanse($"X-Admin-Api-Key: {SecretValue}");

        Assert.DoesNotContain(SecretValue, cleansed);
    }

    [Fact]
    public void Redacts_a_named_credential_in_json()
    {
        var cleansed = LogMessageCleanser.Cleanse($$"""{"password": "{{SecretValue}}"}""");

        Assert.DoesNotContain(SecretValue, cleansed);
    }

    [Theory]
    [InlineData("posted to https://discord.com/api/webhooks/123456789/" + SecretValue)]
    [InlineData("posted to https://discordapp.com/api/webhooks/123456789/" + SecretValue)]
    [InlineData("posted to https://api.telegram.org/bot" + SecretValue + "/sendMessage")]
    public void Redacts_a_webhook_url_whose_secret_is_in_the_path(string message)
    {
        // #57 stores webhook targets as secrets in the same sense as a source API key, and their
        // credential lives in the PATH, where the query-string patterns cannot see it.
        var cleansed = LogMessageCleanser.Cleanse(message);

        Assert.DoesNotContain(SecretValue, cleansed);
        Assert.Contains(LogMessageCleanser.Replacement, cleansed);
    }

    /// <summary>
    /// arb-7j5x: every arm redacts a SECOND occurrence of the same shape, including the
    /// cleanser-only <c>WebhookUrl</c> arm that the shared corpus cannot cover.
    ///
    /// <para>Each test above plants exactly one value per shape, so a mutant in which each arm
    /// replaces only its FIRST match passed this entire file — measured. The planted secret in each
    /// row below is the fragment MEASURED to survive that mutant. Note the query row uses two
    /// SEPARATE urls rather than two parameters of one url: in <c>?apikey=A&amp;token=B</c> the
    /// later <c>NamedCredential</c> arm redacts <c>B</c> on its own first match, so that shape
    /// passes under the mutant and would be a vacuous plant.</para>
    /// </summary>
    [Theory]
    [InlineData(
        "GET http://192.0.2.10/a?apikey=" + SecretValue + " then GET http://192.0.2.10/b?apikey=" + SecondSecretValue + " failed",
        SecondSecretValue)]
    [InlineData(
        "Authorization: Bearer " + SecretValue + " retried as Bearer " + SecondSecretValue,
        SecondSecretValue)]
    [InlineData(
        "X-Admin-Api-Key: " + SecretValue + " and client_secret=" + SecondSecretValue + " expired",
        SecondSecretValue)]
    [InlineData(
        "posted to https://discord.com/api/webhooks/123456789/" + SecretValue
            + " and https://api.telegram.org/bot" + SecondSecretValue + "/sendMessage",
        SecondSecretValue)]
    public void A_second_occurrence_of_the_same_shape_is_redacted_too(string message, string secret)
    {
        // POSITIVE CONTROL: the second occurrence really is in the input.
        Assert.Contains(secret, message, StringComparison.Ordinal);

        var cleansed = LogMessageCleanser.Cleanse(message);

        Assert.Contains(LogMessageCleanser.Replacement, cleansed!, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, cleansed!, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_ordinary_text_alone()
    {
        // A cleanser that mangles normal lines is its own outage: an operator reading redacted
        // gibberish cannot diagnose anything, so the patterns require a credential-shaped NAME
        // rather than matching any long token.
        const string message = "Search for 'The Expanse S01E01' returned 42 results in 1.3s from source NZBHydra2";

        Assert.Equal(message, LogMessageCleanser.Cleanse(message));
    }

    [Fact]
    public void Leaves_a_release_guid_alone()
    {
        const string message = "Suppressed release 8f14e45f-ceea-467a-9575-9a1d0f0f0f0f by rule 'no-cam'";

        Assert.Equal(message, LogMessageCleanser.Cleanse(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Passes_null_and_empty_through(string? input)
    {
        Assert.Equal(input, LogMessageCleanser.Cleanse(input));
    }

    // arb-mw7: the input-length cap. A credential in the HEAD is redacted and the truncation marker
    // is appended; a credential entirely in the DROPPED TAIL never reaches the output (positive
    // control: assert the raw input contains it before asserting the output does not); a credential
    // straddling the cut leaves at most a few leading characters, because the cap scrubs a small
    // overscrub margin past the cut before truncating.

    [Fact]
    public void Input_over_the_cap_is_redacted_in_the_head_and_carries_the_truncation_marker()
    {
        var head = $"apikey={SecretValue} " + new string('x', LogMessageCleanser.MaxCleanseInputLength + 500);
        var cleansed = LogMessageCleanser.Cleanse(head);

        Assert.NotNull(cleansed);
        Assert.DoesNotContain(SecretValue, cleansed);
        Assert.Contains(LogMessageCleanser.Replacement, cleansed);
        Assert.EndsWith(LogMessageCleanser.TruncationMarker, cleansed, StringComparison.Ordinal);
        Assert.True(cleansed!.Length <= LogMessageCleanser.MaxCleanseInputLength + LogMessageCleanser.TruncationMarker.Length);
    }

    [Fact]
    public void A_credential_entirely_in_the_dropped_tail_does_not_appear()
    {
        var padding = new string('x', LogMessageCleanser.MaxCleanseInputLength + 500);
        var input = $"{padding} apikey={SecretValue}";

        // Positive control: the raw input really does carry the secret before Cleanse runs.
        Assert.Contains(SecretValue, input);

        var cleansed = LogMessageCleanser.Cleanse(input);

        Assert.NotNull(cleansed);
        Assert.DoesNotContain(SecretValue, cleansed);
        // Proves the tail was DROPPED, not redacted: a redaction would have left the Replacement
        // token where the credential was; dropping leaves neither the secret nor a marker for it.
        Assert.DoesNotContain(LogMessageCleanser.Replacement, cleansed);
    }

    [Fact]
    public void A_credential_straddling_the_cut_leaves_at_most_a_few_leading_characters()
    {
        // Position the credential so it straddles the boundary: the `apikey=` PREFIX starts
        // overlap + prefix.Length (= 12) characters before MaxCleanseInputLength, which puts the
        // start of the VALUE itself `overlap` (= 5) characters before the cap, with the rest of the
        // value extending well past it. The arithmetic is overlap minus the prefix length, so
        // changing either constant moves the boundary — keep both in view when editing.
        const int overlap = 5;
        var prefix = "apikey=";
        var startAt = LogMessageCleanser.MaxCleanseInputLength - overlap;
        var padding = new string('x', startAt - prefix.Length);
        var input = $"{padding}{prefix}{SecretValue}";

        var cleansed = LogMessageCleanser.Cleanse(input);

        Assert.NotNull(cleansed);
        // The overscrub margin (64 chars past the cap) fully covers this credential, so it is
        // redacted before the cut discards the tail — no fragment of the secret should survive.
        var secretPrefix = SecretValue[..Math.Min(3, SecretValue.Length)];
        Assert.DoesNotContain(secretPrefix, cleansed, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, cleansed, StringComparison.Ordinal);

        // Proves REDACTION rather than mere absence, without over-claiming at this position. The
        // credential IS matched and replaced during the overscrub pass, but the pattern keeps its
        // `apikey=` prefix and only substitutes the value, so Replacement begins `overlap` (= 5)
        // characters before the cap — and the cut at MaxCleanseInputLength then bisects the token
        // itself, leaving "apikey=<reda" here rather than a whole "<redacted>". Asserting the FULL
        // token would fail for the right reason and is why this asserts the surviving marker prefix:
        // absence of the value alone would also pass had the tail merely been dropped, which is the
        // different code path A_credential_entirely_in_the_dropped_tail_does_not_appear covers.
        var markerPrefix = LogMessageCleanser.Replacement[..Math.Min(5, LogMessageCleanser.Replacement.Length)];
        Assert.Contains(prefix + markerPrefix, cleansed, StringComparison.Ordinal);
    }

    [Fact]
    public void Input_exactly_at_the_limit_is_not_truncated()
    {
        var input = new string('x', LogMessageCleanser.MaxCleanseInputLength);

        var cleansed = LogMessageCleanser.Cleanse(input);

        Assert.Equal(input, cleansed);
        Assert.DoesNotContain(LogMessageCleanser.TruncationMarker, cleansed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cut_landing_inside_an_astral_character_does_not_leave_a_lone_surrogate()
    {
        // U+1F510 CLOSED LOCK WITH KEY: outside the BMP, so it is TWO UTF-16 code units. Padding to
        // one short of the cap puts its high half at MaxCleanseInputLength - 1 and its low half at
        // MaxCleanseInputLength, so the cut falls exactly between them. The trailing padding is what
        // pushes the input past the cap so the truncation path runs at all.
        const string astral = "\U0001F510";
        var input = new string('x', LogMessageCleanser.MaxCleanseInputLength - 1)
            + astral
            + new string('y', 500);

        // Positive control on the SETUP: assert the boundary really is mid-pair, so this test cannot
        // pass by accidentally never exercising the case it is named for.
        Assert.True(char.IsHighSurrogate(input[LogMessageCleanser.MaxCleanseInputLength - 1]));
        Assert.True(char.IsLowSurrogate(input[LogMessageCleanser.MaxCleanseInputLength]));

        // Positive control on the ASSERTION: the un-nudged cut — what this code did before arb-59gf —
        // computed inline rather than by mutating the class, to demonstrate the assertion below would
        // actually fail against the old behaviour. An absence assertion that nothing could ever
        // violate proves nothing.
        var unNudged = input[..LogMessageCleanser.MaxCleanseInputLength];
        Assert.Contains(unNudged, char.IsSurrogate);
        Assert.Throws<EncoderFallbackException>(
            () => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetBytes(unNudged));

        var cleansed = LogMessageCleanser.Cleanse(input);

        Assert.NotNull(cleansed);
        Assert.EndsWith(LogMessageCleanser.TruncationMarker, cleansed, StringComparison.Ordinal);

        // The marker itself is BMP-only, so any surrogate in the output would have to come from the
        // cut — no lone half, and the whole string encodes as UTF-8 without a fallback.
        Assert.DoesNotContain(cleansed!, char.IsSurrogate);
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetBytes(cleansed!);
    }

    // arb-qafw: a regex timeout on one text degrades to a fixed placeholder for THAT text only.
    // Cleanse cannot be made to time out from outside without an injectable probe (the compiled
    // arms' matchTimeoutMilliseconds is fixed at compile time), so this drives the test-only
    // overload the same way SanitizedErrorDescription's timeoutProbe does.

    [Fact]
    public void A_regex_timeout_degrades_to_the_timeout_placeholder()
    {
        var cleansed = LogMessageCleanser.Cleanse("anything", _ => throw new RegexMatchTimeoutException());

        Assert.Equal(LogMessageCleanser.TimeoutPlaceholder, cleansed);
    }

    [Fact]
    public void The_timeout_probe_is_not_used_when_null()
    {
        var cleansed = LogMessageCleanser.Cleanse("ordinary text", timeoutProbe: null);

        Assert.Equal("ordinary text", cleansed);
    }
}
