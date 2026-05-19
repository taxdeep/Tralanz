namespace Citus.SysAdmin.Api.Control;

/// <summary>
/// PUT body for
/// <c>/control/companies/{companyId}/transfer-ownership</c>.
/// <see cref="ToMembershipId"/> is the new owner. <see cref="Reason"/>
/// is required at the workflow layer; the endpoint passes whatever
/// the SysAdmin operator typed verbatim.
/// </summary>
public sealed record CompanyOwnershipTransferRequest(
    Guid ToMembershipId,
    string Reason);
