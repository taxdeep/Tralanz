namespace Aiseworks.DesktopShell;

public sealed class ConnectionSettingsDraft
{
    public string ServerHost { get; init; } = "localhost";

    public int BusinessPort { get; init; } = 18080;

    public int AccountingApiPort { get; init; } = 15088;

    public int SysAdminPort { get; init; } = 18090;

    public int SysAdminApiPort { get; init; } = 15089;

    public int PostgresPort { get; init; } = 55432;

    public List<string> RecentServers { get; init; } = [];

    public static ConnectionSettingsDraft FromState(ConnectionSettingsState state)
    {
        return new ConnectionSettingsDraft
        {
            ServerHost = state.ServerHost,
            BusinessPort = state.BusinessPort,
            AccountingApiPort = state.AccountingApiPort,
            SysAdminPort = state.SysAdminPort,
            SysAdminApiPort = state.SysAdminApiPort,
            PostgresPort = state.PostgresPort,
            RecentServers = state.RecentServers.ToList()
        };
    }

    public ConnectionSettingsState ToState(string? updatedBy)
    {
        return new ConnectionSettingsState
        {
            ServerHost = ServerHost.Trim(),
            BusinessPort = BusinessPort,
            AccountingApiPort = AccountingApiPort,
            SysAdminPort = SysAdminPort,
            SysAdminApiPort = SysAdminApiPort,
            PostgresPort = PostgresPort,
            RecentServers = RecentServers.ToList(),
            UpdatedAt = DateTimeOffset.Now,
            UpdatedBy = updatedBy
        };
    }
}

public sealed class ConnectionSettingsState
{
    public string ServerHost { get; init; } = "localhost";

    public int BusinessPort { get; init; } = 18080;

    public int AccountingApiPort { get; init; } = 15088;

    public int SysAdminPort { get; init; } = 18090;

    public int SysAdminApiPort { get; init; } = 15089;

    public int PostgresPort { get; init; } = 55432;

    public List<string> RecentServers { get; init; } = [];

    public DateTimeOffset? UpdatedAt { get; init; }

    public string? UpdatedBy { get; init; }

    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(ServerHost)
            && IsValidPort(BusinessPort)
            && IsValidPort(AccountingApiPort)
            && IsValidPort(SysAdminPort)
            && IsValidPort(SysAdminApiPort)
            && IsValidPort(PostgresPort);
    }

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;
}

public sealed class DesktopBusinessUserContext
{
    public string DisplayName { get; init; } = "";

    public string Email { get; init; } = "";

    public IReadOnlyList<string> Roles { get; init; } = [];

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(DisplayName)
        || !string.IsNullOrWhiteSpace(Email)
        || Roles.Count > 0;
}
