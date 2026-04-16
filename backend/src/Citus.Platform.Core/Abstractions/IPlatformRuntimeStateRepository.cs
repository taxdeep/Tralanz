using Citus.Platform.Core.Runtime;

namespace Citus.Platform.Core.Abstractions;

public interface IPlatformRuntimeStateRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<PlatformMaintenanceState?> GetMaintenanceStateAsync(CancellationToken cancellationToken);

    Task<PlatformMaintenanceState> UpsertMaintenanceStateAsync(
        PlatformMaintenanceState state,
        CancellationToken cancellationToken);

    Task<PlatformNotificationReadinessState?> GetNotificationReadinessStateAsync(CancellationToken cancellationToken);

    Task<PlatformNotificationReadinessState> UpsertNotificationReadinessStateAsync(
        PlatformNotificationReadinessState state,
        CancellationToken cancellationToken);
}
