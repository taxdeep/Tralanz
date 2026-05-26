using Citus.Modules.UnitySearch.Application.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// AI-side concrete <see cref="IUnitysearchQueryIntentBackfillEnqueuer"/>.
/// Dispatches each enqueue onto a Task.Run that opens its own DI scope,
/// resolves <see cref="IUnitysearchQueryIntentBackfillService"/>, and
/// performs the backfill. Errors are swallowed at this boundary — the
/// search hot path must never see them.
///
/// Concurrency bound: each call spawns one task. The
/// TryReservePendingAsync de-dup inside the backfill service prevents
/// thundering herd at the DB layer, so spawning many concurrent tasks
/// for the same (company, query) collapses to one LLM call.
/// </summary>
public sealed class UnitysearchQueryIntentBackfillEnqueuer : IUnitysearchQueryIntentBackfillEnqueuer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UnitysearchQueryIntentBackfillEnqueuer> _logger;

    public UnitysearchQueryIntentBackfillEnqueuer(
        IServiceScopeFactory scopeFactory,
        ILogger<UnitysearchQueryIntentBackfillEnqueuer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Enqueue(
        CompanyId companyId,
        string normalizedQuery,
        string queryHash,
        IReadOnlyList<string> allowedEntityTypes)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery) || string.IsNullOrWhiteSpace(queryHash))
        {
            return;
        }

        // Capture a copy so the user's request scope can dispose without
        // racing the background task.
        var allowed = allowedEntityTypes?.ToArray() ?? Array.Empty<string>();

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var backfill = scope.ServiceProvider
                    .GetRequiredService<IUnitysearchQueryIntentBackfillService>();

                // Use a fresh cancellation token — the user's request
                // token is already cancelled by the time this runs.
                await backfill.BackfillForQueryAsync(
                    companyId, normalizedQuery, queryHash, allowed, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Last-ditch catch so unhandled exceptions in the
                // background task never reach the process unhandled-
                // exception handler. The backfill service has its own
                // inner catches; this is just a belt-and-suspenders.
                _logger.LogWarning(ex,
                    "Background query-intent backfill failed for {Company} '{Query}'.",
                    companyId, normalizedQuery);
            }
        });
    }
}
