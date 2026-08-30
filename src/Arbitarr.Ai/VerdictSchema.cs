namespace Arbitarr.Ai;

/// <summary>
/// The strict JSON Schema sent as Ollama's <c>format</c> field (constrained decoding), forcing
/// the model's output into exactly one shape: <c>{"verdict": "accept"|"reject", "confidence": 0.0-1.0}</c>.
/// This is Ollama's structured-output mode, not the older free-text <c>"json"</c> mode string —
/// the model cannot emit any other field or an out-of-range confidence.
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
