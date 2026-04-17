using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Shell;
using Microsoft.Extensions.Options;
using Modules.CompanyAccess.SessionContext;
using SharedKernel.CompanyAccess;
using Web.Shell.Configuration;
using Web.Shell.Services;

namespace Web.Shell;

public static class BusinessSessionApi
{
    public static IEndpointRouteBuilder MapBusinessSessionApi(this IEndpointRouteBuilder endpoints)
    {
        var sessions = endpoints.MapGroup("/api/business/session");

        sessions.MapPost("/sign-in", SignInAsync);
        sessions.MapPost("/mfa/complete", CompleteSecondFactorAsync);
        sessions.MapGet(string.Empty, GetSessionAsync);
        sessions.MapGet("/context", GetContextAsync);
        sessions.MapPut("/active-company", SwitchActiveCompanyAsync);
        sessions.MapPost("/sign-out", SignOutAsync);

        return endpoints;
    }

    private static async Task<IResult> SignInAsync(
        SignInRequest request,
        HttpContext httpContext,
        IOptions<WebShellAppHostOptions> options,
        IPlatformBusinessSessionRepository repository,
        ICompanySessionContextWorkflow workflow,
        IPlatformRuntimeStateRepository runtimeStateRepository,
        CancellationToken cancellationToken)
    {
        var maintenance = await runtimeStateRepository.GetMaintenanceStateAsync(cancellationToken);
        if (maintenance?.Enabled == true)
        {
            return Results.Json(
                new ErrorResponse("Business sign-in is blocked while maintenance mode is enabled."),
                statusCode: StatusCodes.Status423Locked);
        }

        var result = await repository.AuthenticateAsync(
            request.Login,
            request.Password,
            TimeSpan.FromHours(Math.Max(options.Value.BusinessSessionHours, 1)),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken);

        return await ToAuthResultAsync(
            result,
            repository,
            workflow,
            runtimeStateRepository,
            cancellationToken,
            includeSessionToken: true);
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext httpContext,
        IPlatformBusinessSessionRepository repository,
        ICompanySessionContextWorkflow workflow,
        IPlatformRuntimeStateRepository runtimeStateRepository,
        CancellationToken cancellationToken)
    {
        if (!TryReadSessionToken(httpContext, out var sessionToken, out var error))
        {
            return error!;
        }

        var result = await repository.ValidateSessionAsync(sessionToken, cancellationToken);
        return await ToAuthResultAsync(
            result,
            repository,
            workflow,
            runtimeStateRepository,
            cancellationToken,
            includeSessionToken: false);
    }

    private static async Task<IResult> CompleteSecondFactorAsync(
        CompleteSecondFactorRequest request,
        HttpContext httpContext,
        IOptions<WebShellAppHostOptions> options,
        IPlatformBusinessSessionRepository repository,
        ICompanySessionContextWorkflow workflow,
        IPlatformRuntimeStateRepository runtimeStateRepository,
        CancellationToken cancellationToken)
    {
        var result = await repository.CompleteSecondFactorAsync(
            request.ChallengeId,
            request.VerificationCode,
            TimeSpan.FromHours(Math.Max(options.Value.BusinessSessionHours, 1)),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken);

        return await ToAuthResultAsync(
            result,
            repository,
            workflow,
            runtimeStateRepository,
            cancellationToken,
            includeSessionToken: true);
    }

    private static async Task<IResult> GetContextAsync(
        HttpContext httpContext,
        IPlatformBusinessSessionRepository repository,
        ICompanySessionContextWorkflow workflow,
        IPlatformRuntimeStateRepository runtimeStateRepository,
        CancellationToken cancellationToken)
    {
        if (!TryReadSessionToken(httpContext, out var sessionToken, out var error))
        {
            return error!;
        }

        var result = await repository.ValidateSessionAsync(sessionToken, cancellationToken);
        if (!result.Succeeded)
        {
            return ToFailureResult(result);
        }

        if (result.RequiresSecondFactor)
        {
            return Results.Ok(new WebShellBusinessSignInResponse
            {
                AuthenticationStage = string.IsNullOrWhiteSpace(result.AuthenticationStage)
                    ? "challenge_required"
                    : result.AuthenticationStage,
                RequiresSecondFactor = true,
                MfaChallengeId = result.MfaChallengeId?.ToString("D"),
                MfaChallengeExpiresAtUtc = result.MfaChallengeExpiresAtUtc,
                AvailableSecondFactors = result.AvailableSecondFactors
            });
        }

        var context = await BuildContextAsync(
            result.UserId,
            result.ActiveCompanyId,
            workflow,
            runtimeStateRepository,
            cancellationToken);
        if (context is null)
        {
            await repository.RevokeSessionAsync(sessionToken, cancellationToken);
            return Results.Unauthorized();
        }

        return Results.Ok(context);
    }

    private static async Task<IResult> SwitchActiveCompanyAsync(
        SwitchActiveCompanyRequest request,
        HttpContext httpContext,
        IPlatformBusinessSessionRepository repository,
        ICompanySessionContextWorkflow workflow,
        IPlatformRuntimeStateRepository runtimeStateRepository,
        CancellationToken cancellationToken)
    {
        if (!TryReadSessionToken(httpContext, out var sessionToken, out var error))
        {
            return error!;
        }

        var result = await repository.SwitchActiveCompanyAsync(
            sessionToken,
            request.CompanyId,
            cancellationToken);

        return await ToAuthResultAsync(
            result,
            repository,
            workflow,
            runtimeStateRepository,
            cancellationToken,
            includeSessionToken: false,
            sessionTokenOverride: sessionToken);
    }

    private static async Task<IResult> SignOutAsync(
        HttpContext httpContext,
        IPlatformBusinessSessionRepository repository,
        CancellationToken cancellationToken)
    {
        if (!TryReadSessionToken(httpContext, out var sessionToken, out _))
        {
            return Results.NoContent();
        }

        await repository.RevokeSessionAsync(sessionToken, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ToAuthResultAsync(
        PlatformBusinessSessionResult result,
        IPlatformBusinessSessionRepository repository,
        ICompanySessionContextWorkflow workflow,
        IPlatformRuntimeStateRepository runtimeStateRepository,
        CancellationToken cancellationToken,
        bool includeSessionToken,
        string? sessionTokenOverride = null)
    {
        if (!result.Succeeded)
        {
            return ToFailureResult(result);
        }

        if (result.RequiresSecondFactor)
        {
            return Results.Ok(new WebShellBusinessSignInResponse
            {
                AuthenticationStage = string.IsNullOrWhiteSpace(result.AuthenticationStage)
                    ? "challenge_required"
                    : result.AuthenticationStage,
                RequiresSecondFactor = true,
                MfaChallengeId = result.MfaChallengeId?.ToString("D"),
                MfaChallengeExpiresAtUtc = result.MfaChallengeExpiresAtUtc,
                AvailableSecondFactors = result.AvailableSecondFactors
            });
        }

        var context = await BuildContextAsync(
            result.UserId,
            result.ActiveCompanyId,
            workflow,
            runtimeStateRepository,
            cancellationToken);
        if (context is null)
        {
            var tokenToRevoke = string.IsNullOrWhiteSpace(sessionTokenOverride)
                ? result.SessionToken
                : sessionTokenOverride;
            await repository.RevokeSessionAsync(tokenToRevoke, cancellationToken);
            return Results.Json(
                new ErrorResponse("Business company context is unavailable for this account."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        return includeSessionToken
            ? Results.Ok(new WebShellBusinessSignInResponse
            {
                SessionToken = result.SessionToken,
                Context = context,
                ExpiresAtUtc = result.ExpiresAtUtc,
                AuthenticationStage = string.IsNullOrWhiteSpace(result.AuthenticationStage)
                    ? "authenticated"
                    : result.AuthenticationStage
            })
            : Results.Ok(new WebShellBusinessSessionStateResponse
            {
                Context = context,
                ExpiresAtUtc = result.ExpiresAtUtc
            });
    }

    private static IResult ToFailureResult(PlatformBusinessSessionResult result) =>
        result.FailureCode switch
        {
            "invalid_credentials" or "missing_session" or "invalid_session" or "expired_session" or "account_not_active" or "account_locked"
                => Results.Json(
                    new ErrorResponse(result.FailureMessage),
                    statusCode: StatusCodes.Status401Unauthorized),
            "no_company_access" => Results.Json(
                new ErrorResponse(result.FailureMessage),
                statusCode: StatusCodes.Status403Forbidden),
            "mfa_not_ready" or "mfa_delivery_failed" => Results.Json(
                new ErrorResponse(result.FailureMessage),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.BadRequest(new ErrorResponse(result.FailureMessage))
        };

    private static async Task<BusinessSessionContextSummary?> BuildContextAsync(
        Guid userId,
        Guid activeCompanyId,
        ICompanySessionContextWorkflow workflow,
        IPlatformRuntimeStateRepository runtimeStateRepository,
        CancellationToken cancellationToken)
    {
        var context = await workflow.GetAsync(userId, activeCompanyId, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var maintenance = await runtimeStateRepository.GetMaintenanceStateAsync(cancellationToken);
        return new BusinessSessionContextSummary
        {
            User = new BusinessUserSummary
            {
                Id = context.User.Id,
                DisplayName = context.User.DisplayName,
                Email = context.User.Email,
                Username = context.User.Username,
                Roles = context.User.Roles.ToArray()
            },
            ActiveCompany = MapCompany(context.ActiveCompany),
            AvailableCompanies = context.AvailableCompanies.Select(MapCompany).ToArray(),
            MaintenanceState = maintenance is null
                ? new MaintenanceStateSummary
                {
                    Enabled = false,
                    Message = "Platform runtime is accepting interactive changes."
                }
                : new MaintenanceStateSummary
                {
                    Enabled = maintenance.Enabled,
                    Message = maintenance.Message,
                    ScheduledUntilUtc = maintenance.ScheduledUntilUtc
                }
        };
    }

    private static BusinessCompanySummary MapCompany(CompanyAccessCompanySummary company) =>
        new()
        {
            Id = company.Id,
            CompanyCode = company.CompanyCode,
            CompanyName = company.CompanyName,
            BaseCurrencyCode = company.BaseCurrencyCode,
            MultiCurrencyEnabled = company.MultiCurrencyEnabled,
            Status = NormalizeStatus(company.Status),
            IsReadOnly = company.IsReadOnly
        };

    private static bool TryReadSessionToken(
        HttpContext httpContext,
        out string sessionToken,
        out IResult? error)
    {
        if (!httpContext.Request.Headers.TryGetValue(BusinessAuthHeaderNames.SessionToken, out var values))
        {
            sessionToken = string.Empty;
            error = Results.Unauthorized();
            return false;
        }

        sessionToken = values.FirstOrDefault()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            error = Results.Unauthorized();
            return false;
        }

        error = null;
        return true;
    }

    private static string NormalizeStatus(string? status) =>
        string.IsNullOrWhiteSpace(status)
            ? "inactive"
            : status.Trim().ToLowerInvariant();

    private sealed record SignInRequest(string Login, string Password);

    private sealed record CompleteSecondFactorRequest(Guid ChallengeId, string VerificationCode);

    private sealed record SwitchActiveCompanyRequest(Guid CompanyId);

    private sealed record ErrorResponse(string Error);
}
