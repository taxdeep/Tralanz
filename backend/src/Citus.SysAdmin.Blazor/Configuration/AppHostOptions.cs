namespace Citus.SysAdmin.Blazor.Configuration;

public sealed class AppHostOptions
{
    public const string SectionName = "AppHost";

    public string PathBase { get; set; } = "/sysadmin";

    public string SysAdminApiBaseUrl { get; set; } = "http://127.0.0.1:5089/";

    public string AccountingApiBaseUrl { get; set; } = "http://127.0.0.1:5088/";

    public string BusinessAppBaseUrl { get; set; } = "http://127.0.0.1:8080/";

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
