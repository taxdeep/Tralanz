using Citus.Platform.Core.Runtime;
using Citus.Ui.Shared.Shell;

namespace Citus.SysAdmin.Api.Control;

public static class MaintenanceStateMappings
{
    public static PlatformMaintenanceState ToPlatformMaintenanceState(this MaintenanceStateSummary state) =>
        new()
        {
            Enabled = state.Enabled,
            Message = state.Message,
            ScheduledUntilUtc = state.ScheduledUntilUtc
        };

    public static MaintenanceStateSummary ToSummary(this PlatformMaintenanceState state) =>
        new()
        {
            Enabled = state.Enabled,
            Message = state.Message,
            ScheduledUntilUtc = state.ScheduledUntilUtc
        };
}
