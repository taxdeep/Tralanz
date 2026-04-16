namespace Web.Shell.Configuration;

public sealed class WebShellCompanyOption
{
    public Guid Id { get; set; }

    public string CompanyCode { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;

    public string BaseCurrencyCode { get; set; } = string.Empty;

    public bool MultiCurrencyEnabled { get; set; }

    public string Status { get; set; } = "active";

    public bool IsReadOnly { get; set; }
}
