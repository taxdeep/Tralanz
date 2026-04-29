namespace Citus.SysAdmin.Api.Control;

/// <summary>
/// PUT /control/operations/ai-provider body. NewApiKey is the only
/// secret — null preserves the existing encrypted envelope, non-empty
/// rewrites it. ClearApiKey=true is the explicit "remove the key"
/// path. Mirrors the SmtpConfigHttpRequest contract.
/// </summary>
public sealed record AiProviderConfigHttpRequest(
    string? Provider,
    string? BaseUrl,
    string? Model,
    int? MaxTokens,
    double? Temperature,
    string? NewApiKey,
    bool? ClearApiKey);
