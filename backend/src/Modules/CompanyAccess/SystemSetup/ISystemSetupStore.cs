namespace Modules.CompanyAccess.SystemSetup;

public interface ISystemSetupStore
{
    Task<SystemSetupPreference> GetAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<SystemSetupPreference> SaveAsync(
        Guid userId,
        SharedKernel.CompanyAccess.NumberDisplayMode numberDisplayMode,
        CancellationToken cancellationToken);
}
