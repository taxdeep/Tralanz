using System.Reflection;

namespace Citus.Accounting.Api;

/// <summary>
/// Cross-company / cross-user safety net for minimal-API request
/// bodies. Every body argument is reflectively scanned for properties
/// named <c>CompanyId</c> / <c>UserId</c>:
///
/// <list type="bullet">
///   <item>If a strongly-typed <see cref="CompanyId"/> / <see cref="UserId"/>
///     property is present, its value must equal the active session's
///     value.</item>
///   <item><b>H-7 (PR-H2)</b>: if a property with one of those names
///     exists but its type is NOT the strongly-typed wrapper (e.g. a
///     raw <c>Guid</c> or <c>string</c>), the request is rejected
///     loudly. Pre-PR-H2 the guard silently no-op'd, which would have
///     let a future contract with <c>Guid CompanyId</c> act on any
///     company the caller could spell — a cross-company leak surface
///     dependent only on programmer discipline. The new check fails
///     fast on the first request so the wrong-typed property is
///     impossible to deploy.</item>
/// </list>
/// </summary>
public sealed class BusinessRequestContractGuard
{
    public BusinessRequestGuardResult Validate(IReadOnlyList<object?> arguments, BusinessSessionContext session)
    {
        foreach (var argument in arguments)
        {
            if (argument is null)
            {
                continue;
            }

            var type = argument.GetType();

            // CompanyId guard.
            //
            // Both the non-nullable strong type AND its Nullable<> wrapper are
            // accepted — Nullable.GetUnderlyingType unwraps the latter so the
            // guard still trusts the strongly-typed id. The original strict
            // check was meant to refuse string / Guid CompanyId properties; it
            // accidentally rejected legitimate Nullable<CompanyId> contracts.
            // The value-comparison path below already tolerates null
            // (pattern-match fall-through), so semantics are preserved:
            // a contract that doesn't send a CompanyId skips the cross-
            // company check, and the rest of the request flows through the
            // session-bound active company.
            var companyIdProperty = type.GetProperty("CompanyId", BindingFlags.Instance | BindingFlags.Public);
            if (companyIdProperty is not null)
            {
                var underlyingCompanyIdType = Nullable.GetUnderlyingType(companyIdProperty.PropertyType)
                                              ?? companyIdProperty.PropertyType;
                if (underlyingCompanyIdType != typeof(CompanyId))
                {
                    return BusinessRequestGuardResult.Reject(
                        $"Contract {type.Name} declares a 'CompanyId' property of type " +
                        $"'{companyIdProperty.PropertyType.Name}', but the cross-company guard only " +
                        $"trusts the strongly-typed CompanyId (or its Nullable<> wrapper). Rename or " +
                        $"change the property's type so the safety net can verify it.");
                }

                var rawValue = companyIdProperty.GetValue(argument);
                if (rawValue is CompanyId typedCompanyId && !typedCompanyId.Equals(session.ActiveCompanyId))
                {
                    return BusinessRequestGuardResult.Reject(
                        $"Request company '{typedCompanyId}' does not match the active company context '{session.ActiveCompanyId}'.");
                }
            }

            // UserId guard — same shape, same Nullable<> tolerance.
            var userIdProperty = type.GetProperty("UserId", BindingFlags.Instance | BindingFlags.Public);
            if (userIdProperty is not null)
            {
                var underlyingUserIdType = Nullable.GetUnderlyingType(userIdProperty.PropertyType)
                                            ?? userIdProperty.PropertyType;
                if (underlyingUserIdType != typeof(UserId))
                {
                    return BusinessRequestGuardResult.Reject(
                        $"Contract {type.Name} declares a 'UserId' property of type " +
                        $"'{userIdProperty.PropertyType.Name}', but the cross-user guard only trusts the " +
                        $"strongly-typed UserId (or its Nullable<> wrapper). Rename or change the " +
                        $"property's type so the safety net can verify it.");
                }

                var rawValue = userIdProperty.GetValue(argument);
                if (rawValue is UserId typedUserId && !typedUserId.Equals(session.UserId))
                {
                    return BusinessRequestGuardResult.Reject(
                        $"Request user '{typedUserId}' does not match the authenticated business session '{session.UserId}'.");
                }
            }
        }

        return BusinessRequestGuardResult.Allow();
    }
}
