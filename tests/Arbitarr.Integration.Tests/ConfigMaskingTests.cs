using System.Text.RegularExpressions;
using Arbitarr.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M2-3: given a persisted configuration that includes an NZBHydra2 API key and a LAN base URL,
/// <c>/api/config/effective</c>'s response body must contain neither — not masked, not partial:
/// <see cref="ConfigProjection"/> (src/Arbitarr.Api/Dashboard/ConfigProjection.cs) is a hard
/// allow-list that never has a property for either field, so this test proves the whole-response
/// body against the same credential/RFC-1918 regex shapes the repo's pre-commit secret guard
/// (.githooks/pre-commit) enforces on commits, as an independent runtime check.
/// </summary>
public sealed partial class ConfigMaskingTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public ConfigMaskingTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Effective_config_response_contains_no_credential_or_lan_address_values()
    {
        // The values below never came from real infrastructure — they mirror the *shape* of a
        // real NZBHydra2 API key and a LAN base URL only closely enough to trip the same regexes
        // the pre-commit hook uses, so this test would fail loudly if ConfigProjection ever grew
        // a field that leaked them. The URL uses the RFC 5737 documentation range (192.0.2.0/24,
        // TEST-NET-1) per this repo's own secret-guard convention rather than a real RFC 1918
        // address, so it deliberately does NOT match PrivateLanAddressPattern below — the guard
        // against real topology leaking is DoesNotContain(fakeLanUrl, body).
        const string fakeApiKey = "sk-live-9f3a7c2e8b1d4f60";
        const string fakeLanUrl = "http://192.0.2.50:5076";

        await _factory.SeedAsync(db =>
        {
            // No SettingEntry key exists for either value (ConfigProjection has no such field to
            // populate them from), so this seeds the *closest* thing to a real leak vector: a
            // NzbHydraConfigurationStatus-shaped row is not persisted here because that status is
            // supplied via DI (IsConfigured: false) rather than the settings table (see
            // src/Arbitarr.Api/Dashboard/NzbHydraConfigurationStatus.cs and Program.cs). Seeding a
            // decoy Settings row under an unrecognized name proves EffectiveSettingsReader (which
            // only reads known SettingKey names) can't accidentally surface it either.
            db.Settings.Add(new SettingEntry { Name = "NzbHydraApiKey", Value = fakeApiKey });
            db.Settings.Add(new SettingEntry { Name = "NzbHydraBaseUrl", Value = fakeLanUrl });
            return Task.CompletedTask;
        });

        using var client = _factory.CreateClient();
        var body = await client.GetStringAsync("/api/config/effective");

        Assert.DoesNotContain(fakeApiKey, body);
        Assert.DoesNotContain(fakeLanUrl, body);
        Assert.DoesNotContain("192.0.2", body);
        Assert.False(PrivateLanAddressPattern().IsMatch(body), $"Response body matched RFC 1918 pattern: {body}");
        Assert.False(CredentialLikePattern().IsMatch(body), $"Response body matched credential pattern: {body}");
    }

    // Mirrors .githooks/pre-commit's RFC 1918 detector (192.168.x.x / 10.x.x.x / 172.16-31.x.x).
    [GeneratedRegex(@"\b(192\.168\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2[0-9]|3[01])\.\d{1,3}\.\d{1,3})\b")]
    private static partial Regex PrivateLanAddressPattern();

    // Mirrors .githooks/pre-commit's credential-looking-value detector (key/token/secret/password
    // markers followed by a plausible secret value).
    [GeneratedRegex(@"(apikey|api_key|password|passwd|secret|token)[""'\s]*[=:]\s*[""']?[A-Za-z0-9+/_-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialLikePattern();
}
