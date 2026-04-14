using SharedKernel.CompanyAccess;

namespace Modules.CompanyAccess.SystemSetup;

public sealed class SystemSetupWorkflow : ISystemSetupWorkflow
{
    private readonly ISystemSetupStore _store;

    public SystemSetupWorkflow(ISystemSetupStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<SystemSetupPreference> GetAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        _store.GetAsync(userId, cancellationToken);

    public Task<SystemSetupPreference> SaveNumberDisplayModeAsync(
        Guid userId,
        string modeCode,
        CancellationToken cancellationToken)
    {
        if (!NumberDisplayModeDefaults.TryParseCode(modeCode, out var mode))
        {
            throw new InvalidOperationException($"Unknown number display mode '{modeCode}'.");
        }

        return _store.SaveAsync(userId, mode, cancellationToken);
    }
}
