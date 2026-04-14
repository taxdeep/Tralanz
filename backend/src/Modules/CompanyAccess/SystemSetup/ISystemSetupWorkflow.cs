namespace Modules.CompanyAccess.SystemSetup;

public interface ISystemSetupWorkflow
{
    Task<SystemSetupPreference> GetAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<SystemSetupPreference> SaveNumberDisplayModeAsync(
        Guid userId,
        string modeCode,
        CancellationToken cancellationToken);
}
