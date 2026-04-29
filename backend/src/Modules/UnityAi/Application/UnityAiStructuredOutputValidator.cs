using System.Text.Json;
using Citus.Modules.UnityAi.Application.Contracts;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Per-task JSON shape validation for AI provider output. The gateway
/// feeds raw JSON in; we either return null (= valid, gateway proceeds
/// to deserialize into TOutput) or a short error string (= gateway logs
/// the invalid_output row and returns Failed to the caller).
///
/// Today the registry is keyed by task type and only checks structural
/// expectations — required keys, type primitives, length bounds. Full
/// schema-pinning (JSON Schema or System.Text.Json contract validation)
/// is a follow-up; the goal at this layer is to fail fast on garbage so
/// the dynamic deserialization in <see cref="UnityAiGateway"/> doesn't
/// throw on a malformed provider response.
/// </summary>
public sealed class UnityAiStructuredOutputValidator : IUnityAiStructuredOutputValidator
{
    public string? Validate(string taskType, string outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return "Empty output JSON.";
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(outputJson);
        }
        catch (JsonException ex)
        {
            return $"Output is not valid JSON: {ex.Message}";
        }

        using var _ = doc;

        return taskType switch
        {
            UnityAiPromptRegistry.UnitysearchRerankV1 => ValidateRerankV1(doc.RootElement),
            _ => null, // Unknown task: accept. Adding a strict allowlist
                       // would force every internal task ever shipped to
                       // be re-registered here on top of the prompt
                       // registry — too much friction for too little gain.
        };
    }

    /// <summary>
    /// Expected shape:
    /// <code>
    /// {
    ///   "hints": [
    ///     { "target_entity_id": "<uuid string>",
    ///       "boost": 0..3,
    ///       "confidence": 0..1,
    ///       "reason": "..." },
    ///     ...
    ///   ]
    /// }
    /// </code>
    /// Empty <c>hints</c> array is valid (the AI may legitimately return
    /// "I have nothing useful to add"). Cap is enforced loosely so a
    /// runaway response doesn't blow up the distillation job.
    /// </summary>
    private static string? ValidateRerankV1(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "Root must be a JSON object.";
        }

        if (!root.TryGetProperty("hints", out var hintsElement))
        {
            return "Missing required 'hints' array.";
        }

        if (hintsElement.ValueKind != JsonValueKind.Array)
        {
            return "'hints' must be an array.";
        }

        const int maxHints = 200;
        var index = 0;
        foreach (var hint in hintsElement.EnumerateArray())
        {
            if (index >= maxHints)
            {
                return $"Too many hints (limit {maxHints}).";
            }

            if (hint.ValueKind != JsonValueKind.Object)
            {
                return $"hints[{index}] must be an object.";
            }

            if (!hint.TryGetProperty("target_entity_id", out var idEl) ||
                idEl.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(idEl.GetString(), out _))
            {
                return $"hints[{index}].target_entity_id must be a uuid string.";
            }

            if (!hint.TryGetProperty("boost", out var boostEl) ||
                boostEl.ValueKind != JsonValueKind.Number ||
                !boostEl.TryGetDecimal(out var boost) ||
                boost < 0m || boost > 3m)
            {
                return $"hints[{index}].boost must be a number in [0, 3].";
            }

            if (!hint.TryGetProperty("confidence", out var confEl) ||
                confEl.ValueKind != JsonValueKind.Number ||
                !confEl.TryGetDecimal(out var conf) ||
                conf < 0m || conf > 1m)
            {
                return $"hints[{index}].confidence must be a number in [0, 1].";
            }

            // Reason is optional in spirit but required structurally so
            // the distillation job can populate UnitysearchRankingHintRecord.Reason.
            if (!hint.TryGetProperty("reason", out var reasonEl) ||
                reasonEl.ValueKind != JsonValueKind.String)
            {
                return $"hints[{index}].reason must be a string.";
            }

            index++;
        }

        return null;
    }
}
