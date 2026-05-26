using Citus.Modules.UnityAi.Domain.Shared;
using Microsoft.Extensions.Configuration;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Reads unityAI feature flags from <see cref="IConfiguration"/>. Falls back
/// to the safe defaults declared in <see cref="UnityAiFeatureFlagDefaults"/>.
/// Truthy values: 1, true, yes, on (case-insensitive).
/// </summary>
public sealed class UnityAiFeatureFlagAccessor
{
    private readonly IConfiguration _configuration;

    public UnityAiFeatureFlagAccessor(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool GatewayEnabled => Bool(UnityAiFeatureFlagKeys.AiGatewayEnabled, UnityAiFeatureFlagDefaults.AiGatewayEnabled);

    public bool EmbeddingsEnabled => Bool(UnityAiFeatureFlagKeys.EmbeddingsEnabled, UnityAiFeatureFlagDefaults.EmbeddingsEnabled);

    public bool UnitysearchLearningEnabled =>
        Bool(UnityAiFeatureFlagKeys.SmartPickerLearningEnabled, UnityAiFeatureFlagDefaults.SmartPickerLearningEnabled);

    public bool UnitysearchAiLearningEnabled =>
        Bool(UnityAiFeatureFlagKeys.SmartPickerAiLearningEnabled, UnityAiFeatureFlagDefaults.SmartPickerAiLearningEnabled);

    public bool UnitysearchAiHintAutoApply =>
        Bool(UnityAiFeatureFlagKeys.SmartPickerAiHintAutoApply, UnityAiFeatureFlagDefaults.SmartPickerAiHintAutoApply);

    public bool UnitysearchTraceEnabled =>
        Bool(UnityAiFeatureFlagKeys.SmartPickerTraceEnabled, UnityAiFeatureFlagDefaults.SmartPickerTraceEnabled);

    public double UnitysearchTraceSampleRate =>
        Double(UnityAiFeatureFlagKeys.SmartPickerDecisionTraceSampleRate, UnityAiFeatureFlagDefaults.SmartPickerDecisionTraceSampleRate);

    public bool ReportUsageLearningEnabled =>
        Bool(UnityAiFeatureFlagKeys.ReportUsageLearningEnabled, UnityAiFeatureFlagDefaults.ReportUsageLearningEnabled);

    public bool DashboardRecommendationEnabled =>
        Bool(UnityAiFeatureFlagKeys.DashboardRecommendationEnabled, UnityAiFeatureFlagDefaults.DashboardRecommendationEnabled);

    public bool ActionCenterEnabled =>
        Bool(UnityAiFeatureFlagKeys.ActionCenterEnabled, UnityAiFeatureFlagDefaults.ActionCenterEnabled);

    public bool ActionCenterAiTaskSuggestionsEnabled =>
        Bool(UnityAiFeatureFlagKeys.ActionCenterAiTaskSuggestionsEnabled, UnityAiFeatureFlagDefaults.ActionCenterAiTaskSuggestionsEnabled);

    public bool OcrEnabled => Bool(UnityAiFeatureFlagKeys.OcrEnabled, UnityAiFeatureFlagDefaults.OcrEnabled);

    public bool CopilotEnabled => Bool(UnityAiFeatureFlagKeys.CopilotEnabled, UnityAiFeatureFlagDefaults.CopilotEnabled);

    public string? DefaultProvider => _configuration[UnityAiFeatureFlagKeys.DefaultProvider];

    private bool Bool(string key, bool defaultValue)
    {
        var raw = _configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => defaultValue,
        };
    }

    private double Double(string key, double defaultValue)
    {
        var raw = _configuration[key];
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }
}
