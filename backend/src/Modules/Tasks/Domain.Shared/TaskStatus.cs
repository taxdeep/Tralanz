namespace Citus.Modules.Tasks.Domain.Shared;

/// <summary>
/// Lifecycle states defined by Tralance Task Module Authority Summary.
/// Storage uses the lowercase string form; <see cref="ToToken"/> and
/// <see cref="TryParse"/> handle the boundary.
/// </summary>
public enum TaskStatus
{
    /// <summary>Default state after creation — fully editable.</summary>
    Open = 0,

    /// <summary>Marked ready-to-bill; lines frozen but not yet posted.</summary>
    Completed = 1,

    /// <summary>An AR invoice has been posted from this task (set by AR).</summary>
    Billed = 2,

    /// <summary>Terminal: the task was cancelled before billing.</summary>
    Canceled = 3,
}

public static class TaskStatusExtensions
{
    public static string ToToken(this TaskStatus status) => status switch
    {
        TaskStatus.Open => "open",
        TaskStatus.Completed => "completed",
        TaskStatus.Billed => "billed",
        TaskStatus.Canceled => "canceled",
        _ => throw new InvalidOperationException($"Unknown task status '{status}'."),
    };

    public static bool TryParse(string? token, out TaskStatus status)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case "open": status = TaskStatus.Open; return true;
            case "completed": status = TaskStatus.Completed; return true;
            case "billed": status = TaskStatus.Billed; return true;
            case "canceled":
            case "cancelled": status = TaskStatus.Canceled; return true;
            default: status = default; return false;
        }
    }

    public static TaskStatus Parse(string token) =>
        TryParse(token, out var status)
            ? status
            : throw new InvalidOperationException($"Unknown task status token '{token}'.");
}
