namespace Web.Shell.Configuration;

public sealed class WebShellAppHostOptions
{
    public const string SectionName = "AppHost";

    public string AccountingApiBaseUrl { get; set; } = "http://127.0.0.1:5088/";

    public Guid BootstrapUserId { get; set; } = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");

    public string BootstrapUserDisplayName { get; set; } = "Alice Rowan";

    public string BootstrapUserEmail { get; set; } = "alice.rowan@northwind.example";

    public string BootstrapUsername { get; set; } = "alice.rowan";

    public string[] BootstrapRoles { get; set; } = ["owner", "reports"];

    public Guid? DefaultActiveCompanyId { get; set; } = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");

    public WebShellCompanyOption[] Companies { get; set; } =
    [
        new()
        {
            Id = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc"),
            CompanyCode = "NORTHWIND",
            CompanyName = "Northwind Studio Ltd.",
            BaseCurrencyCode = "USD",
            MultiCurrencyEnabled = true
        }
    ];
}
