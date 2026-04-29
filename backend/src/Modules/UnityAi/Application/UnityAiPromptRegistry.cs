using Citus.Modules.UnityAi.Application.Contracts;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// In-memory prompt registry. Keys are <c>(taskType, version)</c>. When the
/// caller asks for a task without specifying a version, the registry
/// returns the entry registered as the "default" version for that task —
/// today the only version, with versioning hooked up later when we need
/// to A/B prompts.
///
/// Prompts live in code (not config) on purpose: prompt text is part of
/// the system's behavior contract, must be reviewed in PRs, and rolling
/// back a regression should be a code revert — not a database edit.
/// </summary>
public sealed class UnityAiPromptRegistry : IUnityAiPromptRegistry
{
    /// <summary>
    /// Task type for the UnitySearch hint distillation flow. Reads recent
    /// (anchor, target) pair-stat data + entity display names; AI returns
    /// a list of boost recommendations the distillation job persists into
    /// <c>unitysearch_ranking_hints</c>.
    /// </summary>
    public const string UnitysearchRerankV1 = "unitysearch.rerank.v1";

    private readonly Dictionary<string, PromptTemplate> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _defaultVersionByTask = new(StringComparer.OrdinalIgnoreCase);

    public UnityAiPromptRegistry()
    {
        RegisterDefault(new PromptTemplate(
            TaskType: UnitysearchRerankV1,
            Version: "v1",
            SystemPrompt:
                "You are a search-relevance expert for accounting software (chart of accounts, " +
                "vendors, customers, items). You will receive a JSON object describing one " +
                "company's most-clicked search candidates inside a specific (context, " +
                "entity_type) bucket, including each candidate's display name and 30-day " +
                "click count. Your job is to suggest small relevance boosts for candidates " +
                "whose display name implies they are commonly used by typical small " +
                "businesses, even when click frequency alone wouldn't justify a boost (cold " +
                "start, new entity). Boosts are additive on top of click-history scoring; " +
                "the deterministic engine already handles raw frequency. Constraints: boost " +
                "in [0, 3], confidence in [0, 1], at most 8 hints per call, skip any candidate " +
                "you are not confident about. Be conservative — an empty hints list is a " +
                "valid answer. Respond with strict JSON only, no commentary, matching this " +
                "shape: {\"hints\":[{\"target_entity_id\":\"<uuid from input>\",\"boost\":<0-3>," +
                "\"confidence\":<0-1>,\"reason\":\"<short, one sentence>\"}]}",
            UserPromptTemplate: "Input:\n" + UnityAiGateway.InputJsonToken,
            ResponseSchemaName: "unitysearch.rerank.v1.response"));
    }

    public PromptTemplate? Get(string taskType, string? requestedVersion)
    {
        if (string.IsNullOrWhiteSpace(taskType)) return null;

        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            return _defaultVersionByTask.TryGetValue(taskType, out var defaultVersion)
                && _byKey.TryGetValue(Key(taskType, defaultVersion), out var defaultTemplate)
                ? defaultTemplate
                : null;
        }

        return _byKey.TryGetValue(Key(taskType, requestedVersion!), out var template) ? template : null;
    }

    private void RegisterDefault(PromptTemplate template)
    {
        _byKey[Key(template.TaskType, template.Version)] = template;
        _defaultVersionByTask[template.TaskType] = template.Version;
    }

    private static string Key(string taskType, string version) => $"{taskType}::{version}";
}
