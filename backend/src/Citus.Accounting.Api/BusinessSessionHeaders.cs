using Citus.Ui.Shared.Business;

namespace Citus.Accounting.Api;

public static class BusinessSessionHeaders
{
    public const string UserId = BusinessSessionHeaderNames.UserId;

    public const string ActiveCompanyId = BusinessSessionHeaderNames.ActiveCompanyId;
}
