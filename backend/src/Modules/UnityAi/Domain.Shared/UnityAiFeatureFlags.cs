namespace Citus.Modules.UnityAi.Domain.Shared;

/// <summary>
/// Names of the unityAI feature flags. Read from <c>IConfiguration</c>;
/// also accept env-var-style upper-case keys.
///
/// Defaults are conservative — see <see cref="UnityAiFeatureFlagDefaults"/>.
/// </summary>
public static class UnityAiFeatureFlagKeys
{
    public const string AiGatewayEnabled = "UNITYAI_GATEWAY_ENABLED";
    /// <summary>
    /// Plan C-Population: gates the embedding provider + back-fill +
    /// query-embedding cache. Independent of AiGatewayEnabled so the
    /// operator can keep chat-completion-driven features (intent
    /// distillation, ranking hints) running while turning embeddings
    /// off — or vice versa. Default false: opt-in.
    /// </summary>
    public const string EmbeddingsEnabled = "UNITYAI_EMBEDDINGS_ENABLED";
    public const string SmartPickerLearningEnabled = "UNITYSEARCH_LEARNING_ENABLED";
    public const string SmartPickerAiLearningEnabled = "UNITYSEARCH_AI_LEARNING_ENABLED";
    public const string SmartPickerAiHintAutoApply = "UNITYSEARCH_AI_HINT_AUTO_APPLY";
    public const string SmartPickerTraceEnabled = "UNITYSEARCH_TRACE_ENABLED";
    public const string SmartPickerDecisionTraceSampleRate = "UNITYSEARCH_DECISION_TRACE_SAMPLE_RATE";
    public const string ReportUsageLearningEnabled = "REPORT_USAGE_LEARNING_ENABLED";
    public const string DashboardRecommendationEnabled = "DASHBOARD_RECOMMENDATION_ENABLED";
    public const string ActionCenterEnabled = "ACTION_CENTER_ENABLED";
    public const string ActionCenterAiTaskSuggestionsEnabled = "AI_TASK_SUGGESTIONS_ENABLED";
    public const string OcrEnabled = "UNITYAI_OCR_ENABLED";
    public const string CopilotEnabled = "UNITYAI_COPILOT_ENABLED";

    public const string DefaultProvider = "AI_DEFAULT_PROVIDER";
    public const string DefaultCheapModel = "AI_DEFAULT_CHEAP_MODEL";
    public const string DefaultAdvancedModel = "AI_DEFAULT_ADVANCED_MODEL";
    public const string DefaultVisionModel = "AI_DEFAULT_VISION_MODEL";
    public const string MaxCostPerJob = "AI_MAX_COST_PER_JOB";
    public const string MaxRequestsPerJob = "AI_MAX_REQUESTS_PER_JOB";
}

public static class UnityAiFeatureFlagDefaults
{
    public const bool AiGatewayEnabled = false;
    public const bool EmbeddingsEnabled = false;
    public const bool SmartPickerLearningEnabled = true;
    public const bool SmartPickerAiLearningEnabled = false;
    public const bool SmartPickerAiHintAutoApply = false;
    public const bool SmartPickerTraceEnabled = false;
    public const double SmartPickerDecisionTraceSampleRate = 0.0;
    public const bool ReportUsageLearningEnabled = true;
    public const bool DashboardRecommendationEnabled = true;
    public const bool ActionCenterEnabled = true;
    public const bool ActionCenterAiTaskSuggestionsEnabled = false;
    public const bool OcrEnabled = false;
    public const bool CopilotEnabled = false;
}
