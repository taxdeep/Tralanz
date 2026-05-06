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

            if (TryReadCompanyIdProperty(type, argument, "CompanyId", out var companyId) &&
                !companyId.Equals(session.ActiveCompanyId))
            {
                return BusinessRequestGuardResult.Reject(
                    $"Request company '{companyId}' does not match the active company context '{session.ActiveCompanyId}'.");
            }

            if (TryReadUserIdProperty(type, argument, "UserId", out var userId) &&
                !userId.Equals(session.UserId))
            {
                return BusinessRequestGuardResult.Reject(
                    $"Request user '{userId}' does not match the authenticated business session '{session.UserId}'.");
            }
        }

        return BusinessRequestGuardResult.Allow();
    }

    private static bool TryReadCompanyIdProperty(Type type, object instance, string propertyName, out CompanyId value)
    {
        value = default;

        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.PropertyType != typeof(CompanyId))
        {
            return false;
        }

        var rawValue = property.GetValue(instance);
        if (rawValue is not CompanyId typedValue)
        {
            return false;
        }

        value = typedValue;
        return true;
    }

    private static bool TryReadUserIdProperty(Type type, object instance, string propertyName, out UserId value)
    {
        value = default;

        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.PropertyType != typeof(UserId))
        {
            return false;
        }

        var rawValue = property.GetValue(instance);
        if (rawValue is not UserId typedValue)
        {
            return false;
        }

        value = typedValue;
        return true;
    }
}
