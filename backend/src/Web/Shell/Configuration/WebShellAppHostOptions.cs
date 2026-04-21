namespace Web.Shell.Configuration;

public sealed class WebShellAppHostOptions
{
    public const string SectionName = "AppHost";

    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:3000/";

    public string AccountingApiBaseUrl { get; set; } = "http://127.0.0.1:5088/";

    public int BusinessSessionHours { get; set; } = 12;

    public Guid BootstrapUserId { get; set; }

    public string BootstrapUserDisplayName { get; set; } = string.Empty;

    public string BootstrapUserEmail { get; set; } = string.Empty;

    public string BootstrapUsername { get; set; } = string.Empty;

    public string[] BootstrapRoles { get; set; } = [];

    public Guid? DefaultActiveCompanyId { get; set; }

    public bool DisableRazorComponents { get; set; }

    public WebShellCompanyOption[] Companies { get; set; } = [];
}
