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

    /// <summary>
    /// arb-p4r: greedy decoding. Classification verdicts feed a cache keyed on model identity; a
    /// non-zero temperature makes the same title/model/prompt combination non-reproducible, so a
    /// cache hit or a re-run cannot be trusted to agree with the original call.
    /// </summary>
    public const double SamplingTemperature = 0;

    /// <summary>
    /// arb-p4r: fixed seed, paired with <see cref="SamplingTemperature"/> so a deterministic decode
    /// also has a deterministic starting point. The value itself is arbitrary — it only needs to be
    /// fixed. Changing it does not need a manual invalidation step: since arb-qg3o it feeds
    /// <see cref="DecodingIdentity"/>, which is folded into the verdict cache key.
    /// </summary>
    public const int SamplingSeed = 42;

    /// <summary>
    /// arb-qg3o: a stable, human-readable token identifying the decoding constants above, folded
    /// into the verdict cache key as <c>AiModelIdentity.DecodingIdentity</c> so that changing either
    /// constant invalidates previously cached verdicts BY CONSTRUCTION.
    ///
    /// <para>
    /// This replaces arb-p4r's interim lever, which was a bump of the default
    /// <c>Arbitarr:Ai:PromptVersion</c>. That lever had two holes this closes: it could be defeated
    /// by an operator who pinned <c>Arbitarr:Ai:PromptVersion</c> explicitly, and it overloaded a
    /// term documented to mean the prompt TEMPLATE version. Nothing here reads configuration, so
    /// there is no setting that can suppress the invalidation.
    /// </para>
    ///
    /// <para>
    /// Formatted invariantly, so a machine with a comma decimal separator cannot produce a
    /// different token — and therefore a different cache key — for identical constants.
    /// </para>
    /// </summary>
    public static string DecodingIdentity =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"t{SamplingTemperature}-s{SamplingSeed}");
}
