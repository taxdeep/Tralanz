using SharedKernel.CompanyAccess;

namespace Modules.CompanyAccess.SystemSetup;

public sealed record SystemSetupPreference(
    Guid UserId,
    NumberDisplayMode NumberDisplayMode,
    DateTimeOffset UpdatedAt);
