namespace Citus.Platform.Core.Abstractions;

/// <summary>
/// SysAdmin-managed AI provider configuration. One row, encrypted
/// api_key, mirroring the pattern set by IPlatformSmtpConfigStore.
/// The runtime IUnityAiProvider implementations (OpenAI / Anthropic /
/// Azure) read through here so operators can rotate keys, switch
/// providers, or disable AI completely without redeploying.
/// </summary>
public interface IPlatformAiProviderConfigStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<PlatformAiProviderConfigSnapshot?> GetAsync(CancellationToken cancellationToken);

    Task<PlatformAiProviderConfigSnapshot> UpsertAsync(
        PlatformAiProviderConfigUpsertRequest request,
        UserId updatedByUserId,
        CancellationToken cancellationToken);

    /// <summary>Returns the encrypted envelope so the connection-test
    /// path can decrypt + use the key. Internal-ish helper —  consumers
    /// in the API layer go through this rather than the snapshot's
    /// HasApiKey flag.</summary>
    Task<string?> GetRawApiKeyAsync(CancellationToken cancellationToken);
}

public sealed record PlatformAiProviderConfigSnapshot(
    string Provider,
    string? BaseUrl,
    string Model,
    int MaxTokens,
    double Temperature,
    bool HasApiKey,
    DateTimeOffset UpdatedAt,
    UserId? UpdatedByUserId);

public sealed record PlatformAiProviderConfigUpsertRequest(
    string Provider,
    string? BaseUrl,
    string Model,
    int MaxTokens,
    double Temperature,
    string? NewApiKey,
    bool ClearApiKey);

/// <summary>
/// Static catalog of supported providers. Adding a new provider is a
/// matter of dropping a row in here, wiring the test-connection probe
/// for it, and (eventually) adding a runtime IUnityAiProvider impl.
/// </summary>
public static class PlatformAiProviderKeys
{
    public const string Disabled = "disabled";
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string AzureOpenAi = "azure_openai";

    public static readonly string[] All =
    [
        Disabled,
        OpenAi,
        Anthropic,
        AzureOpenAi,
    ];

    public static bool IsValid(string? provider) =>
        provider is not null && Array.IndexOf(All, provider.Trim().ToLowerInvariant()) >= 0;
}
