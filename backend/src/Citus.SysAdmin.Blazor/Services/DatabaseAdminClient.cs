using System.Net.Http.Json;
using Citus.SysAdmin.Blazor.State;
using Citus.Ui.Shared.Control;

namespace Citus.SysAdmin.Blazor.Services;

/// <summary>
/// Thin wrapper around the SysAdmin /control/operations/database
/// endpoints. The Razor page polls list endpoints periodically while
/// a backup or VACUUM is running so the operator sees status flips.
/// </summary>
public sealed class DatabaseAdminClient(
    HttpClient httpClient,
    AppShellState shellState,
    ILogger<DatabaseAdminClient> logger)
{
    public async Task<IReadOnlyList<DatabaseBackupDto>> ListBackupsAsync(
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            var result = await httpClient.GetFromJsonAsync<List<DatabaseBackupDto>>(
                $"control/operations/database/backups?limit={limit}",
                cancellationToken);
            return result ?? new List<DatabaseBackupDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list database backups.");
            return Array.Empty<DatabaseBackupDto>();
        }
    }

    public async Task<DatabaseAdminOutcome<DatabaseBackupDto>> StartBackupAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PostAsync(
                "control/operations/database/backups",
                content: null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return new DatabaseAdminOutcome<DatabaseBackupDto>(false, error, null);
            }

            var record = await response.Content.ReadFromJsonAsync<DatabaseBackupDto>(cancellationToken);
            return new DatabaseAdminOutcome<DatabaseBackupDto>(true, null, record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start database backup.");
            return new DatabaseAdminOutcome<DatabaseBackupDto>(false, ex.Message, null);
        }
    }

    public string BuildBackupDownloadUrl(Guid backupId) =>
        $"control/operations/database/backups/{backupId}/download";

    public async Task<DatabaseAdminOutcome<DatabaseMaintenanceRunDto>> RunVacuumAnalyzeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            using var response = await httpClient.PostAsync(
                "control/operations/database/maintenance/vacuum-analyze",
                content: null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return new DatabaseAdminOutcome<DatabaseMaintenanceRunDto>(false, error, null);
            }

            var run = await response.Content.ReadFromJsonAsync<DatabaseMaintenanceRunDto>(cancellationToken);
            return new DatabaseAdminOutcome<DatabaseMaintenanceRunDto>(true, null, run);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to run VACUUM ANALYZE.");
            return new DatabaseAdminOutcome<DatabaseMaintenanceRunDto>(false, ex.Message, null);
        }
    }

    public async Task<IReadOnlyList<DatabaseMaintenanceRunDto>> ListMaintenanceRunsAsync(
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            var result = await httpClient.GetFromJsonAsync<List<DatabaseMaintenanceRunDto>>(
                $"control/operations/database/maintenance?limit={limit}",
                cancellationToken);
            return result ?? new List<DatabaseMaintenanceRunDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list maintenance runs.");
            return Array.Empty<DatabaseMaintenanceRunDto>();
        }
    }

    public async Task<IReadOnlyList<DatabaseTableSizeDto>> GetTableSizesAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ApplySessionHeader();
            var result = await httpClient.GetFromJsonAsync<List<DatabaseTableSizeDto>>(
                $"control/operations/database/table-sizes?limit={limit}",
                cancellationToken);
            return result ?? new List<DatabaseTableSizeDto>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load table sizes.");
            return Array.Empty<DatabaseTableSizeDto>();
        }
    }

    private void ApplySessionHeader()
    {
        httpClient.DefaultRequestHeaders.Remove(SysAdminAuthConstants.SessionHeaderName);
        if (shellState.IsAuthenticated)
        {
            httpClient.DefaultRequestHeaders.Add(SysAdminAuthConstants.SessionHeaderName, shellState.SessionToken);
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken);
            return body?.Message ?? body?.Detail ?? $"Server returned {(int)response.StatusCode}.";
        }
        catch
        {
            return $"Server returned {(int)response.StatusCode}.";
        }
    }

    private sealed record ErrorBody(string? Message, string? Detail);
}

public sealed record DatabaseBackupDto(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string? FilePath,
    long? SizeBytes,
    UserId TriggeredByUserId,
    string? ErrorMessage);

public sealed record DatabaseMaintenanceRunDto(
    Guid Id,
    string Operation,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    long? DurationMs,
    UserId TriggeredByUserId,
    string? ErrorMessage);

public sealed record DatabaseTableSizeDto(
    string SchemaName,
    string TableName,
    long TotalBytes,
    long DataBytes,
    long IndexBytes,
    long? RowEstimate);

public sealed record DatabaseAdminOutcome<T>(bool Succeeded, string? ErrorMessage, T? Value);
