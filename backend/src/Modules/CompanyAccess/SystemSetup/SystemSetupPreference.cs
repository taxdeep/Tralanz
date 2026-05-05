using SharedKernel.CompanyAccess;

namespace Modules.CompanyAccess.SystemSetup;

public sealed record SystemSetupPreference(
    UserId UserId,
    NumberDisplayMode NumberDisplayMode,
    DateTimeOffset UpdatedAt);
