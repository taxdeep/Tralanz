namespace Modules.CompanyAccess.SystemSetup;

public interface ISystemSetupStore
{
    Task<SystemSetupPreference> GetAsync(
        UserId userId,
        CancellationToken cancellationToken);

    Task<SystemSetupPreference> SaveAsync(
        UserId userId,
        SharedKernel.CompanyAccess.NumberDisplayMode numberDisplayMode,
        CancellationToken cancellationToken);
}
