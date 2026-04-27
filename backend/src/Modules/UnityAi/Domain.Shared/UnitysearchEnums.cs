namespace Citus.Modules.UnityAi.Domain.Shared;

public static class UnitysearchEventType
{
    public const string Search = "search";
    public const string Impression = "impression";
    public const string Select = "select";
    public const string CreateNew = "create_new";
    public const string NoMatch = "no_match";
    public const string Abandon = "abandon";
    public const string Clear = "clear";
    public const string Override = "override";
}

public static class UnitysearchScopeType
{
    public const string Company = "company";
    public const string User = "user";
}

public static class UnitysearchHintSource
{
    public const string System = "system";
    public const string Ai = "ai";
    public const string Admin = "admin";
}

public static class UnitysearchHintStatus
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Superseded = "superseded";
}

public static class UnitysearchHintValidationStatus
{
    public const string Unvalidated = "unvalidated";
    public const string Valid = "valid";
    public const string Invalid = "invalid";
}

public static class UnitysearchLearningProfileSource
{
    public const string System = "system";
    public const string Ai = "ai";
}
