namespace Arbitarr.Core.Ai;

/// <summary>
/// The strict JSON Schema sent as Ollama's <c>format</c> field (constrained decoding), forcing
/// the model's output into exactly one shape: <c>{"verdict": "accept"|"reject", "confidence": 0.0-1.0}</c>.
/// This is Ollama's structured-output mode, not the older free-text <c>"json"</c> mode string —
/// the model cannot emit any other field or an out-of-range confidence.
///
/// <para><b>Why this lives in Arbitarr.Core rather than beside <c>OllamaClient</c> in Arbitarr.Ai
/// (arb-1rr).</b> Two callers now send this schema: the classifier, and
/// <see cref="OllamaConnectivityProber"/>'s <c>/api/chat</c> probe. The probe exists precisely to
/// fail when the real classification request would fail, so it must send the SAME <c>format</c>
/// value — a copy would drift, and the probe would then go green against a schema the classifier no
/// longer sends. The prober is in Arbitarr.Core, which references no other Arbitarr project (AC6,
/// enforced by <c>CoreIsolationTests</c>), so Core cannot reach up into Arbitarr.Ai to share it.
/// Moving the constant DOWN to the shared floor is what lets both send one value; it is a
/// dependency-free <c>const string</c>, so nothing travels with it.</para>
/// </summary>
public static class VerdictSchema
{
    public const string Object = """
        {
          "type": "object",
          "properties": {
            "verdict": {
              "type": "string",
              "enum": ["accept", "reject"]
            },
            "confidence": {
              "type": "number",
              "minimum": 0.0,
              "maximum": 1.0
            }
          },
          "required": ["verdict", "confidence"],
          "additionalProperties": false
        }
        """;
}
