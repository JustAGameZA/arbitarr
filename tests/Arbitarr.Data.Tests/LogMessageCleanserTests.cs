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
}
