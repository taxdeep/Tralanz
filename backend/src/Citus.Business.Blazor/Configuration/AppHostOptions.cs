namespace Citus.Business.Blazor.Configuration;

public sealed class AppHostOptions
{
    public const string SectionName = "AppHost";

    public string PathBase { get; set; } = string.Empty;

    public string AccountingApiBaseUrl { get; set; } = "http://127.0.0.1:5088/";

    public Guid BootstrapUserId { get; set; } = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");

    public string BootstrapUserDisplayName { get; set; } = "Alice Rowan";

    public string BootstrapUserEmail { get; set; } = "alice.rowan@northwind.example";

    public string BootstrapUsername { get; set; } = "alice.rowan";

    public string[] BootstrapRoles { get; set; } = ["owner", "reports"];

    public Guid BootstrapCompanyId { get; set; } = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");

    public string BootstrapCompanyCode { get; set; } = "NORTHWIND";

    public string BootstrapCompanyName { get; set; } = "Northwind Studio Ltd.";

    public string BootstrapCompanyBaseCurrencyCode { get; set; } = "USD";

    public bool BootstrapCompanyMultiCurrencyEnabled { get; set; } = true;

    public static bool HasPathBase(string? pathBase) =>
        !string.IsNullOrWhiteSpace(pathBase) && pathBase != "/";

    public static string NormalizePathBase(string? pathBase)
    {
        if (!HasPathBase(pathBase))
        {
            return string.Empty;
        }

        var normalized = pathBase!.Trim();

        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized.TrimEnd('/');
    }

    public static string NormalizeBaseHref(string? pathBase)
    {
        var normalized = NormalizePathBase(pathBase);
        return string.IsNullOrWhiteSpace(normalized)
            ? "/"
            : normalized + "/";
    }
}
