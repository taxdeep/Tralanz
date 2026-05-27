namespace Aiseworks.ApiClient;

public sealed class AiseworksApiClientOptions
{
    public Uri AccountingApiBaseUrl { get; set; } = new("http://localhost:15088", UriKind.Absolute);

    public Uri BusinessBaseUrl { get; set; } = new("http://localhost:18080", UriKind.Absolute);

    public Uri SysAdminApiBaseUrl { get; set; } = new("http://localhost:15089", UriKind.Absolute);
}
