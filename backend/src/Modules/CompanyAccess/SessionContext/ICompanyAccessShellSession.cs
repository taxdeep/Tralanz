namespace Modules.CompanyAccess.SessionContext;

public interface ICompanyAccessShellSession
{
    Guid CurrentUserId { get; }

    Guid ActiveCompanyId { get; }
}
