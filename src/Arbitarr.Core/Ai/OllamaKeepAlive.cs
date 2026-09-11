using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Arbitarr.Core.Ai;

/// <summary>
/// A validated Ollama <c>keep_alive</c> value, and the single place that knows how it is spelled on
/// the wire.
///
/// <para>Value sent as Ollama's <c>keep_alive</c> field. A long/indefinite duration keeps the model
/// resident between calls, matching the "kept permanently loaded" operational fact recorded in
/// docs/step0-measurements.md (avoids the ~59s cold-load cost recurring per call).</para>
///
/// <para><b>Accepted forms.</b> A bare integer (e.g. <c>"-1"</c>, <c>"300"</c>) — Ollama interprets
/// this as SECONDS, with <c>-1</c> meaning "keep loaded indefinitely" — or a Go duration string
/// carrying an explicit unit (e.g. <c>"-1m"</c>, <c>"30m"</c>).</para>
///
/// <para><b>WHY THE WIRE SHAPE IS TWO SHAPES, NOT ONE (arb-hho, #189).</b> Ollama parses any STRING
/// <c>keep_alive</c> value as a Go duration, so a bare integer sent as a JSON string
/// (<c>"keep_alive":"-1"</c>) is rejected with 400 <c>time: missing unit in duration "-1"</c>. The
/// integer form must therefore be serialised as a JSON NUMBER, while a unit-bearing value is
/// serialised as a JSON STRING verbatim. That rule has already caused one production fault; it is
/// stated once, here, and <see cref="WriteTo"/> is the only implementation of it.</para>
///
/// <para><b>WHY THIS TYPE IS IN CORE (arb-43b).</b> The rule previously existed as two copies of one
/// regex — a private converter in <c>Arbitarr.Ai.OllamaClient</c> and a hand-rolled block in the
/// host's composition root — which is how a rule that has already failed once acquires a second
/// place to drift. Core is the only assembly both can reference: Ai depends on Core, Host depends on
/// both, and Core references nothing above it (<c>CoreIsolationTests</c>).</para>
///
/// <para><b>A COMPOUND DURATION IS REJECTED, DELIBERATELY.</b> Go's own parser accepts
/// <c>1h30m</c>, and this type does not: the pattern anchors exactly one unit. That is the behaviour
/// that shipped, and widening it here would be a silent behaviour change bundled into a refactor
/// whose whole point is that the wire shape does not move. <see cref="TryParse"/> rejecting
/// <c>1h30m</c> is pinned by a test so the limit is explicit rather than incidental; loosening it is
/// a separate, deliberate change.</para>
/// </summary>
public readonly partial record struct OllamaKeepAlive
{
    /// <summary>
    /// The one copy of the duration pattern. A bare integer is NOT matched here — it is accepted by
    /// <see cref="TryParse"/> through <see cref="long.TryParse(string, out long)"/>, because the two
    /// forms serialise differently and telling them apart is exactly this type's job.
    /// </summary>
    [GeneratedRegex(@"^-?\d+(ns|us|µs|ms|s|m|h)$")]
    private static partial Regex DurationPattern();

    private readonly string? _text;

    private OllamaKeepAlive(string text) => _text = text;

    /// <summary>
    /// The built-in default, <c>"-1"</c>: keep the model loaded indefinitely. Also the value a
    /// malformed configured value falls back to.
    /// </summary>
    public static OllamaKeepAlive Default => new("-1");

    /// <summary>
    /// The validated text, exactly as it will be spelled on the wire when it is a string. Never
    /// null: a default-constructed value reads as <see cref="Default"/>'s text, so a struct that
    /// skipped <see cref="TryParse"/> cannot serialise as empty.
    /// </summary>
    public string Text => _text ?? "-1";

    /// <summary>
    /// Whether this value is the bare-integer form, which is serialised as a JSON number rather than
    /// a JSON string.
    /// </summary>
    public bool IsBareInteger => long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Parses a configured <c>keep_alive</c> value. Accepts a bare integer (including a negative
    /// one) or a single-unit Go duration; rejects everything else, including null, empty, whitespace
    /// and a compound duration such as <c>1h30m</c> (see the type's remarks).
    /// </summary>
    /// <param name="text">The configured value, as written.</param>
    /// <param name="value">The parsed value, or <see cref="Default"/> when parsing fails.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> was a usable value.</returns>
    public static bool TryParse([NotNullWhen(true)] string? text, out OllamaKeepAlive value)
    {
        // No trimming, deliberately: " 30m" is not a value Ollama would accept and quietly repairing
        // it here would hide a typo in the operator's configuration rather than warning about it.
        if (!string.IsNullOrEmpty(text)
            && (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                || DurationPattern().IsMatch(text)))
        {
            value = new OllamaKeepAlive(text);
            return true;
        }

        value = Default;
        return false;
    }

    /// <summary>
    /// Writes this value as the JSON token Ollama accepts: a NUMBER for the bare-integer form, a
    /// STRING for a unit-bearing duration. See the type's remarks for why the two differ.
    /// </summary>
    /// <param name="writer">The writer to write the value token to.</param>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var text = Text;
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            writer.WriteNumberValue(integer);
        }
        else
        {
            writer.WriteStringValue(text);
        }
    }

    /// <summary>The validated text — see <see cref="Text"/>.</summary>
    public override string ToString() => Text;
}
