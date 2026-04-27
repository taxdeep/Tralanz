namespace Citus.Modules.UnityAi.Domain.Shared;

public static class DashboardWidgetSource
{
    public const string User = "user";
    public const string Suggestion = "suggestion";
    public const string SystemDefault = "system_default";
}

public static class DashboardSuggestionSource
{
    public const string System = "system";
    public const string Ai = "ai";
}

public static class DashboardSuggestionStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Dismissed = "dismissed";
    public const string Snoozed = "snoozed";
    public const string Expired = "expired";
}
