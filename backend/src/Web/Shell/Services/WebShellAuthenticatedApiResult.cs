namespace Web.Shell.Services;

public sealed record class WebShellAuthenticatedApiResult<T>
{
    public T? Value { get; init; }

    public bool RequiresSignIn { get; init; }

    public bool IsNotFound { get; init; }

    public string? ErrorMessage { get; init; }

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

    public static WebShellAuthenticatedApiResult<T> Failure(string errorMessage) =>
        new() { ErrorMessage = errorMessage };
}
