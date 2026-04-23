namespace Web.Shell.Services;

public sealed record class WebShellAuthenticatedApiResult<T>
{
    public T? Value { get; init; }

    public bool RequiresSignIn { get; init; }

    public bool IsNotFound { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorCode { get; init; }

    public static WebShellAuthenticatedApiResult<T> Success(T? value) =>
        new() { Value = value };

    public static WebShellAuthenticatedApiResult<T> RequiresAuthentication(string? errorMessage = null) =>
        new()
        {
            RequiresSignIn = true,
            ErrorMessage = errorMessage ?? WebShellBusinessSessionClient.AuthenticationRequiredError
        };

    public static WebShellAuthenticatedApiResult<T> NotFound(string? errorMessage = null) =>
        new()
        {
            IsNotFound = true,
            ErrorMessage = errorMessage
        };

    public static WebShellAuthenticatedApiResult<T> Failure(string errorMessage, string? errorCode = null) =>
        new()
        {
            ErrorMessage = errorMessage,
            ErrorCode = errorCode
        };
}

public static class WebShellPostingFeedback
{
    public static string DescribeFailure(string? errorCode, string? errorMessage, string fallbackMessage)
    {
        if (string.Equals(errorCode, "posting_period_closed", StringComparison.Ordinal))
        {
            return "The posting date falls inside a closed period for the active primary book. Move the posting date into an open period or adjust the lock in Book Governance.";
        }

        if (string.Equals(errorCode, "invalid_document_status", StringComparison.Ordinal))
        {
            return "This document is no longer in the right lifecycle state for the requested action. Refresh the page and continue from the current document status.";
        }

        if (string.Equals(errorCode, "not_found", StringComparison.Ordinal))
        {
            return "The requested document could not be found in the active company context. Refresh the workspace and reopen it from the latest list or review page.";
        }

        return string.IsNullOrWhiteSpace(errorMessage)
            ? fallbackMessage
            : errorMessage;
    }
}
