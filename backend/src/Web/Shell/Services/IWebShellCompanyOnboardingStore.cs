namespace Web.Shell.Services;

public interface IWebShellCompanyOnboardingStore
{
    Task<WebShellCompanyOnboardingSummary?> GetAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<WebShellCompanyOnboardingSummary?> AcknowledgeAsync(
        Guid companyId,
        Guid userId,
        CancellationToken cancellationToken);
}
