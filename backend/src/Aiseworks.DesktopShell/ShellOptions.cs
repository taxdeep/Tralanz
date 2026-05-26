namespace Aiseworks.DesktopShell;

public sealed class ShellOptions
{
    public string SelectedServerProfileName { get; init; } = "Local Docker Test Stack";

    public IReadOnlyList<ServerProfileOptions> ServerProfiles { get; init; } = [];

    public string BusinessUrl { get; init; } = "http://localhost:18080";

    public string SysAdminUrl { get; init; } = "http://localhost:18090";

    public string StartupUrl { get; init; } = "http://localhost:18080";

    public string AccountingApiHealthUrl { get; init; } = "http://localhost:15088/health";

    public string BusinessHealthUrl { get; init; } = "http://localhost:18080/system/health";

    public string SysAdminApiHealthUrl { get; init; } = "http://localhost:15089/health";

    public string SysAdminHealthUrl { get; init; } = "http://localhost:18090/system/health";

    public string PostgresHost { get; init; } = "localhost";

    public int PostgresPort { get; init; } = 55432;

    public string RepositoryRoot { get; init; } = "";

    public string DockerProjectName { get; init; } = "aiseworks-test";

    public string DockerComposeFile { get; init; } = "deploy/docker/compose.yml";

    public string DockerEnvFile { get; init; } = "deploy/docker/aiseworks-test.env";
}

public sealed class ServerProfileOptions
{
    public string Name { get; init; } = "Local Docker Test Stack";

    public string Kind { get; init; } = ServerProfileKinds.DockerTestStack;

    public string Description { get; init; } = "";

    public string ServiceName { get; init; } = "";

    public string BusinessUrl { get; init; } = "http://localhost:18080";

    public string SysAdminUrl { get; init; } = "http://localhost:18090";

    public string StartupUrl { get; init; } = "http://localhost:18080";

    public string AccountingApiHealthUrl { get; init; } = "http://localhost:15088/health";

    public string BusinessHealthUrl { get; init; } = "http://localhost:18080/system/health";

    public string SysAdminApiHealthUrl { get; init; } = "http://localhost:15089/health";

    public string SysAdminHealthUrl { get; init; } = "http://localhost:18090/system/health";

    public string PostgresHost { get; init; } = "localhost";

    public int PostgresPort { get; init; } = 55432;
}

public static class ServerProfileKinds
{
    public const string DockerTestStack = "DockerTestStack";

    public const string LocalService = "LocalService";

    public const string LanServer = "LanServer";
}
