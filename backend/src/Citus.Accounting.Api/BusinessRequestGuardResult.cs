namespace Citus.Accounting.Api;

public sealed record class BusinessRequestGuardResult
{
    public bool Allowed { get; init; }

    public int StatusCode { get; init; } = StatusCodes.Status200OK;

    public string Message { get; init; } = string.Empty;

    public BusinessSessionContext? Session { get; init; }

    public BusinessSessionResolution? Resolution { get; init; }

    public static BusinessRequestGuardResult Allow() =>
        new()
        {
            Allowed = true
        };

    public static BusinessRequestGuardResult Reject(string message, int statusCode = StatusCodes.Status400BadRequest) =>
        new()
        {
            Allowed = false,
            StatusCode = statusCode,
            Message = message
        };
}
