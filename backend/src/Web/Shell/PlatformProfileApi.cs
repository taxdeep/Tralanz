using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Ui.Shared.Business;

namespace Web.Shell;

public static class PlatformProfileApi
{
    public static IEndpointRouteBuilder MapPlatformProfileApi(this IEndpointRouteBuilder endpoints)
    {
        var profile = endpoints.MapGroup("/api/platform/profile");

        profile.MapGet(string.Empty, GetAsync);
        profile.MapPut("/display-name", SaveDisplayNameAsync);
        profile.MapPost("/email-change/request", RequestEmailChangeAsync);
        profile.MapPost("/email-change/confirm", ConfirmEmailChangeAsync);
        profile.MapPost("/password-change/request", RequestPasswordChangeAsync);
        profile.MapPost("/password-change/confirm", ConfirmPasswordChangeAsync);

        return endpoints;
    }

    private static Task<IResult> GetAsync(
        HttpContext httpContext,
        IPlatformBusinessSessionRepository businessSessions,
        IPlatformAccountProfileWorkflow workflow,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            httpContext,
            businessSessions,
            (userId, token) => workflow.GetAsync(userId, token),
            cancellationToken);

    private static Task<IResult> SaveDisplayNameAsync(
        SaveDisplayNameRequest request,
        HttpContext httpContext,
        IPlatformBusinessSessionRepository businessSessions,
        IPlatformAccountProfileWorkflow workflow,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            httpContext,
            businessSessions,
            (userId, token) => workflow.SaveDisplayNameAsync(userId, request.DisplayName, token),
            cancellationToken);

    private static Task<IResult> RequestEmailChangeAsync(
        RequestEmailChangeRequest request,
        HttpContext httpContext,
        IPlatformBusinessSessionRepository businessSessions,
        IPlatformAccountProfileWorkflow workflow,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            httpContext,
            businessSessions,
            (userId, token) => workflow.RequestEmailChangeAsync(userId, request.NewEmail, token),
            cancellationToken);

    private static Task<IResult> ConfirmEmailChangeAsync(
        ConfirmEmailChangeRequest request,
        HttpContext httpContext,
        IPlatformBusinessSessionRepository businessSessions,
        IPlatformAccountProfileWorkflow workflow,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            httpContext,
            businessSessions,
            (userId, token) => workflow.ConfirmEmailChangeAsync(userId, request.VerificationCode, token),
            cancellationToken);

    private static Task<IResult> RequestPasswordChangeAsync(
        RequestPasswordChangeRequest request,
        HttpContext httpContext,
        IPlatformBusinessSessionRepository businessSessions,
        IPlatformAccountProfileWorkflow workflow,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            httpContext,
            businessSessions,
            (userId, token) => workflow.RequestPasswordChangeAsync(userId, request.NewPassword, token),
            cancellationToken);

    private static Task<IResult> ConfirmPasswordChangeAsync(
        ConfirmPasswordChangeRequest request,
        HttpContext httpContext,
        IPlatformBusinessSessionRepository businessSessions,
        IPlatformAccountProfileWorkflow workflow,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            httpContext,
            businessSessions,
            (userId, token) => workflow.ConfirmPasswordChangeAsync(userId, request.VerificationCode, token),
            cancellationToken);

    private static async Task<IResult> ExecuteAsync<TResult>(
        HttpContext httpContext,
        IPlatformBusinessSessionRepository businessSessions,
        Func<Guid, CancellationToken, Task<TResult?>> action,
        CancellationToken cancellationToken)
        where TResult : class
    {
        var authentication = await PlatformBusinessSessionAuthorization.TryResolveUserIdAsync(
            httpContext,
            businessSessions,
            cancellationToken);

        if (!authentication.Succeeded)
        {
            return authentication.Error!;
        }

        try
        {
            var result = await action(authentication.UserId, cancellationToken);
            return result is null
                ? Results.NotFound()
                : Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private sealed record SaveDisplayNameRequest(string DisplayName);

    private sealed record RequestEmailChangeRequest(string NewEmail);

    private sealed record ConfirmEmailChangeRequest(string VerificationCode);

    private sealed record RequestPasswordChangeRequest(string NewPassword);

    private sealed record ConfirmPasswordChangeRequest(string VerificationCode);

    private sealed record ErrorResponse(string Error);
}

internal static class PlatformBusinessSessionAuthorization
{
    public static async Task<AuthenticatedBusinessUserResolution> TryResolveUserIdAsync(
        HttpContext httpContext,
        IPlatformBusinessSessionRepository businessSessions,
        CancellationToken cancellationToken)
    {
        if (!httpContext.Request.Headers.TryGetValue(BusinessAuthHeaderNames.SessionToken, out var values))
        {
            return new AuthenticatedBusinessUserResolution(
                false,
                Guid.Empty,
                Results.Unauthorized());
        }

        var sessionToken = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return new AuthenticatedBusinessUserResolution(
                false,
                Guid.Empty,
                Results.Unauthorized());
        }

        var session = await businessSessions.ValidateSessionAsync(sessionToken, cancellationToken);
        if (!session.Succeeded || session.UserId == Guid.Empty)
        {
            return new AuthenticatedBusinessUserResolution(
                false,
                Guid.Empty,
                Results.Unauthorized());
        }

        return new AuthenticatedBusinessUserResolution(
            true,
            session.UserId,
            null);
    }

    internal sealed record AuthenticatedBusinessUserResolution(
        bool Succeeded,
        Guid UserId,
        IResult? Error);
}
