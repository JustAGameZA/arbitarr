using Arbitarr.Core.Ai;

namespace Arbitarr.Ai;

/// <summary>
/// Configuration for <see cref="OllamaClient"/>. The per-call timeout and in-flight concurrency
/// limit are fixed constants from <c>docs/step0-measurements.md</c> §"Inline-budget constant for
/// AI classification calls" — Ollama serializes inference by default, so &gt;1 concurrent request
/// only extends queued wall time without adding throughput (§1 of that document).
/// </summary>
/// <param name="BaseUrl">Base URL of the Ollama instance (e.g. <c>http://192.0.2.138:31434</c> in docs — never a real address).</param>
/// <param name="Model">Model name/tag to request (e.g. <c>qwen2.5:7b-instruct-q4_K_M</c>).</param>
/// <param name="KeepAlive">
/// Value sent as Ollama's <c>keep_alive</c> field, already validated.
///
/// <para>
/// The accepted forms, and the reason the wire shape is a JSON NUMBER for a bare integer but a JSON
/// STRING for a unit-bearing duration, live on <see cref="OllamaKeepAlive"/> — the single
/// implementation of that rule (arb-43b). They are deliberately not restated here: this rule has
/// already caused one production fault (arb-hho, #189), and a second copy of it is how that happens
/// again.
/// </para>
///
/// <para>
/// Defaults to <see langword="default"/>, which reads as <see cref="OllamaKeepAlive.Default"/>
/// (<c>"-1"</c>, keep the model loaded indefinitely) — a C# default parameter must be a compile-time
/// constant, so it cannot name the static property directly.
/// </para>
/// </param>
public sealed record OllamaOptions(Uri BaseUrl, string Model, OllamaKeepAlive KeepAlive = default)
{
    /// <summary>
    /// Per-call timeout: 5 seconds. Warm-path calls were observed at 54-177ms; this gives roughly
    /// 30-60x headroom for longer prompts/outputs without letting one stuck call block the
    /// background worker's queue indefinitely (docs/step0-measurements.md).
    /// </summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum in-flight calls to Ollama at once: 1. Ollama serializes inference by default;
    /// running more than one concurrent request buys no throughput and only extends queued wall
    /// time for every caller (docs/step0-measurements.md §1, concurrency measurement).
    /// </summary>
    public const int MaxInFlight = 1;
}
