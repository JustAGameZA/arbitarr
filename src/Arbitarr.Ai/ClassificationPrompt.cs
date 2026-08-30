using Arbitarr.Core.Releases;

namespace Arbitarr.Ai;

/// <summary>
/// Builds the chat messages sent to Ollama for a single release classification. Protocol-conditioned
/// (AC9/R4): torrent and Usenet junk signals differ, and obfuscated Usenet release names (routine
/// scrambled/random-looking titles produced by some Usenet posting tools) must be read by the model
/// as normal releases, not automatically as junk.
/// </summary>
public static class ClassificationPrompt
{
    /// <summary>
    /// M5 security review (MED): caps how much of the title/category text is echoed into the
    /// prompt sent to Ollama. Neither field is attacker-bounded before reaching this layer, so an
    /// unusually long indexer-supplied title or category list would otherwise inflate the prompt
    /// (and cost/latency) without limit; 512 chars is generous headroom above any real release title.
    /// </summary>
    private const int MaxPromptFieldLength = 512;

    private const string BaseSystemPrompt =
        "You are a release-quality classifier for a media automation tool. Given a single release " +
        "title and its metadata, decide whether it should be accepted (a genuine, well-formed release) " +
        "or rejected (junk: spam, fake, mislabeled, or filler). Respond only via the provided JSON schema.";

    private const string TorrentGuidance =
        "This release came from a torrent indexer. Typical junk signals on torrent indexers include: " +
        "fake/decoy releases with mismatched size-to-quality ratios, password-protected or executable " +
        "payloads implied by the title, scene-tag spoofing, and unrelated bundled content packs.";

    private const string UsenetGuidance =
        "This release came from a Usenet indexer. Usenet posting tools routinely produce titles that " +
        "look obfuscated or randomized (e.g. hash-like segments, scrambled words, unusual casing) as a " +
        "normal, deliberate anti-abuse convention of certain Usenet posting groups — this is NOT, by " +
        "itself, a sign of junk or spam. Judge Usenet releases on structural and metadata signals " +
        "(size plausibility, category match, known-good group/uploader conventions) rather than title " +
        "readability. Do not reject a release merely because its title looks obfuscated or scrambled.";

    /// <summary>
    /// Builds the ordered chat messages (system + user) for a single <paramref name="candidate"/>.
    /// </summary>
    public static IReadOnlyList<OllamaChatMessage> Build(ReleaseCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var protocolGuidance = candidate.Protocol switch
        {
            ProtocolKind.Usenet => UsenetGuidance,
            ProtocolKind.Torrent => TorrentGuidance,
            _ => TorrentGuidance,
        };

        var systemMessage = $"{BaseSystemPrompt} {protocolGuidance}";

        var truncatedTitle = Truncate(candidate.OriginalTitle);
        var truncatedCategories = Truncate(string.Join(",", candidate.Category));

        var userMessage =
            $"Title: {truncatedTitle}\n" +
            $"Protocol: {candidate.Protocol}\n" +
            $"Size (bytes): {candidate.Size}\n" +
            $"Categories: {truncatedCategories}";

        return new[]
        {
            new OllamaChatMessage("system", systemMessage),
            new OllamaChatMessage("user", userMessage),
        };
    }

    private static string Truncate(string value)
        => value.Length <= MaxPromptFieldLength ? value : value[..MaxPromptFieldLength];
}

/// <summary>A single chat message in Ollama's <c>/api/chat</c> request shape.</summary>
public sealed record OllamaChatMessage(string Role, string Content);
