using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Accounts;
using Citus.Ui.Shared.Business;
using Web.Shell.Services;

namespace Web.Shell;

public static class CompanyOnboardingApi
{
    public static IEndpointRouteBuilder MapCompanyOnboardingApi(this IEndpointRouteBuilder endpoints)
    {
        var onboarding = endpoints.MapGroup("/api/company/onboarding");

        onboarding.MapGet("/summary", GetSummaryAsync);
        onboarding.MapPost("/acknowledge", AcknowledgeAsync);

        return endpoints;
    }

    private static async Task<IResult> GetSummaryAsync(
        HttpContext httpContext,
        IPlatformBusinessSessionRepository repository,
        IWebShellCompanyOnboardingStore onboardingStore,
        CancellationToken cancellationToken)
    {
        var session = await ValidateAsync(httpContext, repository, cancellationToken);
        if (session.Error is not null)
        {
            return session.Error;
        }

        var summary = await onboardingStore.GetAsync(session.Result!.ActiveCompanyId, cancellationToken);
        return summary is null
            ? Results.NotFound(new ErrorResponse("Company onboarding summary is unavailable for the active company."))
            : Results.Ok(summary);
    }

    private static async Task<IResult> AcknowledgeAsync(
        HttpContext httpContext,
        IPlatformBusinessSessionRepository repository,
        IWebShellCompanyOnboardingStore onboardingStore,
        CancellationToken cancellationToken)
    {
        var session = await ValidateAsync(httpContext, repository, cancellationToken);
        if (session.Error is not null)
        {
            return session.Error;
        }

        var updated = await onboardingStore.AcknowledgeAsync(
            session.Result!.ActiveCompanyId,
            session.Result.UserId,
            cancellationToken);

        return updated is null
            ? Results.NotFound(new ErrorResponse("Company onboarding summary is unavailable for the active company."))
            : Results.Ok(updated);
    }

    private static async Task<(PlatformBusinessSessionResult? Result, IResult? Error)> ValidateAsync(
        HttpContext httpContext,
        IPlatformBusinessSessionRepository repository,
        CancellationToken cancellationToken)
    {
        if (!httpContext.Request.Headers.TryGetValue(BusinessAuthHeaderNames.SessionToken, out var values))
        {
            return (null, Results.Unauthorized());
        }

        var sessionToken = values.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return (null, Results.Unauthorized());
        }

        var result = await repository.ValidateSessionAsync(sessionToken, cancellationToken);
        if (!result.Succeeded || result.RequiresSecondFactor)
        {
            return (null, Results.Unauthorized());
        }

        return (result, null);
    }

    private sealed record ErrorResponse(string Error);
}
