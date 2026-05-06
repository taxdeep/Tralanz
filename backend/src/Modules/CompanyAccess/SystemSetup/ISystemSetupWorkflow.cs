namespace Modules.CompanyAccess.SystemSetup;

public interface ISystemSetupWorkflow
{
    Task<SystemSetupPreference> GetAsync(
        UserId userId,
        CancellationToken cancellationToken);

    Task<SystemSetupPreference> SaveNumberDisplayModeAsync(
        UserId userId,
        string modeCode,
        CancellationToken cancellationToken);
}
