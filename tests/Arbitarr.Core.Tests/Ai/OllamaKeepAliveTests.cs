using System.Text;
using System.Text.Json;
using Arbitarr.Core.Ai;
using Xunit;

namespace Arbitarr.Core.Tests.Ai;

/// <summary>
/// arb-43b: the wire-shape rule now has exactly one implementation, so it is tested once, here.
/// The rule itself is arb-hho/#189's: Ollama parses any STRING <c>keep_alive</c> as a Go duration,
/// so a bare integer must go on the wire as a JSON NUMBER or the request is rejected with
/// <c>time: missing unit in duration "-1"</c>.
/// </summary>
public class OllamaKeepAliveTests
{
    private static string WriteJson(OllamaKeepAlive value)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static OllamaKeepAlive Parse(string text)
    {
        Assert.True(OllamaKeepAlive.TryParse(text, out var value), $"expected '{text}' to parse");
        return value;
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("300")]
    [InlineData("-300")]
    public void BareIntegersAreWrittenAsAJsonNumber(string text)
    {
        var json = WriteJson(Parse(text));

        // The whole point of the rule: no quotes. A quoted "-1" is what produced the production
        // fault, so this asserts the absence of quoting rather than only the round-tripped value.
        Assert.Equal(text, json);
        Assert.DoesNotContain("\"", json, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Number, JsonDocument.Parse(json).RootElement.ValueKind);
    }

    [Theory]
    [InlineData("-1m")]
    [InlineData("30m")]
    [InlineData("5h")]
    [InlineData("100ms")]
    [InlineData("5µs")]
    [InlineData("10ns")]
    public void UnitBearingDurationsAreWrittenAsAJsonString(string text)
    {
        var json = WriteJson(Parse(text));

        var element = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.String, element.ValueKind);

        // Compared through the PARSED value rather than against the raw bytes: Utf8JsonWriter's
        // default encoder escapes non-ASCII, so "5µs" is written as "5µs". That is valid JSON
        // which decodes back to the original, it is what the previous converter emitted too (it
        // also called WriteStringValue), and it is therefore not a wire change — but asserting on
        // the raw text would have called it one.
        Assert.Equal(text, element.GetString());
    }

    /// <summary>
    /// The escaping noted above, pinned on its own so the behaviour is recorded rather than merely
    /// tolerated by a test that looks through it.
    /// </summary>
    [Fact]
    public void NonAsciiUnitsAreEscapedByTheWriterAndStillDecodeBack()
    {
        var json = WriteJson(Parse("5µs"));

        Assert.Equal("\"5\\u00B5s\"", json);
        Assert.Equal("5µs", JsonDocument.Parse(json).RootElement.GetString());
    }

    [Fact]
    public void ABareIntegerIsNeverEmittedAsAString()
    {
        // The specific regression from arb-hho/#189, stated as its own case.
        var json = WriteJson(Parse("-1"));

        Assert.NotEqual("\"-1\"", json);
        Assert.Equal("-1", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("30x")]
    [InlineData("-1x")]
    [InlineData("1.5h")]
    [InlineData("+5m")]
    [InlineData(" 30m")]
    [InlineData("30m ")]
    [InlineData("m")]
    public void MalformedValuesAreRejectedAndYieldTheDefault(string? text)
    {
        Assert.False(OllamaKeepAlive.TryParse(text, out var value));

        // Rejection still produces a usable value rather than a hole: the caller that ignores the
        // bool (or logs and carries on, as the host does) gets the documented default.
        Assert.Equal("-1", value.Text);
    }

    /// <summary>
    /// Go's own duration parser accepts compound durations such as <c>1h30m</c>; this type does not,
    /// because the pattern it inherited anchors exactly one unit. Pinned so the limit is explicit
    /// rather than incidental — arb-43b moved this rule without widening it, and a future change to
    /// accept compound durations should be a deliberate one that updates this test.
    /// </summary>
    [Theory]
    [InlineData("1h30m")]
    [InlineData("1h0m0s")]
    [InlineData("2m30s")]
    public void CompoundGoDurationsAreRejectedToday(string text)
    {
        Assert.False(OllamaKeepAlive.TryParse(text, out var value));
        Assert.Equal("-1", value.Text);
    }

    [Fact]
    public void DefaultIsMinusOne()
    {
        Assert.Equal("-1", OllamaKeepAlive.Default.Text);
        Assert.Equal("-1", OllamaKeepAlive.Default.ToString());
        Assert.Equal("-1", WriteJson(OllamaKeepAlive.Default));
    }

    [Fact]
    public void ADefaultConstructedValueReadsAsTheDefaultRatherThanEmpty()
    {
        // A struct can always be default-constructed, bypassing TryParse. That must not serialise as
        // an empty string, which Ollama would reject as a duration.
        OllamaKeepAlive uninitialised = default;

        Assert.Equal("-1", uninitialised.Text);
        Assert.Equal("-1", WriteJson(uninitialised));
    }

    [Theory]
    [InlineData("-1", true)]
    [InlineData("300", true)]
    [InlineData("30m", false)]
    [InlineData("-1m", false)]
    public void IsBareIntegerSeparatesTheTwoWireForms(string text, bool expected)
        => Assert.Equal(expected, Parse(text).IsBareInteger);

    [Fact]
    public void ToStringReturnsTheValidatedTextVerbatim()
        => Assert.Equal("30m", Parse("30m").ToString());
}
