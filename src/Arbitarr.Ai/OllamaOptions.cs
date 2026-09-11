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
/// Value sent as Ollama's <c>keep_alive</c> field. A long/indefinite duration keeps the model
/// resident between calls, matching the "kept permanently loaded" operational fact recorded in
/// docs/step0-measurements.md (avoids the ~59s cold-load cost recurring per call).
///
/// <para>
/// Accepted forms: a bare integer (e.g. <c>"-1"</c>, <c>"300"</c>) — Ollama interprets this as
/// SECONDS, with <c>-1</c> meaning "keep loaded indefinitely" — or a Go duration string carrying
/// an explicit unit (e.g. <c>"-1m"</c>, <c>"30m"</c>). Ollama parses any STRING <c>keep_alive</c>
/// value as a Go duration, so a bare integer sent as a JSON string (<c>"keep_alive":"-1"</c>) is
/// rejected with 400 <c>time: missing unit in duration "-1"</c> — the integer form must therefore
/// be serialised as a JSON NUMBER on the wire (<see cref="OllamaClient"/> handles this), while a
/// unit-bearing value is serialised as a JSON string verbatim.
/// </para>
/// </param>
public sealed record OllamaOptions(Uri BaseUrl, string Model, string KeepAlive = "-1")
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
