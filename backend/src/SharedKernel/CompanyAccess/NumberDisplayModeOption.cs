namespace SharedKernel.CompanyAccess;

public sealed record NumberDisplayModeOption(
    NumberDisplayMode Mode,
    string Code,
    string Label,
    string Example,
    string Description);
