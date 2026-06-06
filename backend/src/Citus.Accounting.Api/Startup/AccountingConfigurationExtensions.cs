namespace Citus.Accounting.Api.Startup;

/// <summary>
/// Configuration resolution + validation extracted from Program.cs (P2).
/// Reads the PostgreSQL connection string from CITUS_ACCOUNTING_DB or
/// ConnectionStrings:AccountingCore and fails fast when neither is set.
/// Same keys, same message, same behavior.
/// </summary>
public static class AccountingConfigurationExtensions
{
    public static string ResolveAccountingConnectionString(this WebApplicationBuilder builder)
    {
        var connectionString =
            builder.Configuration["CITUS_ACCOUNTING_DB"] ??
            builder.Configuration.GetConnectionString("AccountingCore");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "A PostgreSQL connection string is required. Configure ConnectionStrings:AccountingCore or CITUS_ACCOUNTING_DB.");
        }
        return connectionString;
    }
}
