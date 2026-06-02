namespace Aiseworks.ServerConsole;

public sealed class ShellOptions
{
    public string SelectedServerProfileName { get; set; } = "Local Docker Test Stack";

    public List<ServerProfileOptions> ServerProfiles { get; set; } = [];

    public string BusinessUrl { get; set; } = "http://localhost:18080";

    public string SysAdminUrl { get; set; } = "http://localhost:18090";

    public string AccountingApiHealthUrl { get; set; } = "http://localhost:15088/health";

    public string BusinessHealthUrl { get; set; } = "http://localhost:18080/system/health";

    public string SysAdminApiHealthUrl { get; set; } = "http://localhost:15089/health";

    public string SysAdminHealthUrl { get; set; } = "http://localhost:18090/system/health";

    public string PostgresHost { get; set; } = "localhost";

    public int PostgresPort { get; set; } = 55432;

    public string RepositoryRoot { get; set; } = "";

    public string DockerProjectName { get; set; } = "aiseworks-test";

    public string DockerComposeFile { get; set; } = "deploy/docker/compose.yml";

    public string DockerEnvFile { get; set; } = "deploy/docker/aiseworks-test.env";
}

public sealed class ServerProfileOptions
{
    public string Name { get; set; } = "Local Docker Test Stack";

    public string Kind { get; set; } = ServerProfileKinds.DockerTestStack;

    public string Description { get; set; } = "";

    public string ServiceName { get; set; } = "";

    public string AccountingApiHealthUrl { get; set; } = "http://localhost:15088/health";

    public string BusinessHealthUrl { get; set; } = "http://localhost:18080/system/health";

    public string SysAdminApiHealthUrl { get; set; } = "http://localhost:15089/health";

    public string SysAdminHealthUrl { get; set; } = "http://localhost:18090/system/health";

    public string PostgresHost { get; set; } = "localhost";

    public int PostgresPort { get; set; } = 55432;
}

public static class ServerProfileKinds
{
    public const string DockerTestStack = "DockerTestStack";

    public const string LocalService = "LocalService";

    public const string LanServer = "LanServer";
}

internal sealed class BackupState
{
    public string? LastBackupFile { get; init; }

    public DateTimeOffset? LastBackupCompletedAt { get; init; }

    public long? LastBackupSizeBytes { get; init; }
}

internal sealed record HealthCheckResult(string Name, string Target, string Status, bool IsHealthy);

internal sealed record ServiceProbeResult(string Status, string Target, string Detail)
{
    public static ServiceProbeResult NotApplicable() =>
        new("N/A", "Current profile", "Service control is not available for this profile.");

    public static ServiceProbeResult NotChecked(string target) =>
        new("Not checked", target, "Service status has not been checked in this session.");
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public static CommandResult Failed(string message) => new(-1, "", message);

    public string ToDisplayText() =>
        $"""
        Exit code: {ExitCode}

        Standard output:
        {StandardOutput}

        Standard error:
        {StandardError}
        """;
}
