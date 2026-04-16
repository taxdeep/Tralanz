namespace Modules.CompanyAccess.SessionContext;

public interface ICompanyAccessShellSession
{
    Guid CurrentUserId { get; }

    Guid ActiveCompanyId { get; }

    bool AreWritesBlocked { get; }

    string WriteBlockMessage { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);
}
