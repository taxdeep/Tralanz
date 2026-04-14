using System.Reflection;

namespace Citus.Accounting.Api;

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

            if (TryReadGuidProperty(type, argument, "CompanyId", out var companyId) &&
                companyId != session.ActiveCompanyId)
            {
                return BusinessRequestGuardResult.Reject(
                    $"Request company '{companyId}' does not match the active company context '{session.ActiveCompanyId}'.");
            }

            if (TryReadGuidProperty(type, argument, "UserId", out var userId) &&
                userId != session.UserId)
            {
                return BusinessRequestGuardResult.Reject(
                    $"Request user '{userId}' does not match the authenticated business session '{session.UserId}'.");
            }
        }

        return BusinessRequestGuardResult.Allow();
    }

    private static bool TryReadGuidProperty(Type type, object instance, string propertyName, out Guid value)
    {
        value = Guid.Empty;

        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.PropertyType != typeof(Guid))
        {
            return false;
        }

        var rawValue = property.GetValue(instance);
        if (rawValue is not Guid guidValue)
        {
            return false;
        }

        value = guidValue;
        return true;
    }
}
