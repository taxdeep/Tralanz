namespace Citus.Modules.Inventory.Application.Contracts;

public interface IInventoryTransferStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<InventoryTransferDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<InventoryTransferSummary> UpsertAsync(
        InventoryTransferUpsertRequest request,
        CancellationToken cancellationToken);

    Task<InventoryTransferSummary> SubmitAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ship the transfer. Accepts an optional <paramref name="idempotencyKey"/>
    /// (sourced from the <c>Idempotency-Key</c> HTTP header) — when provided,
    /// a retried call with the same key on the same company replays the
    /// existing transfer_ship document via
    /// <see cref="InventoryIdempotencyReplayException"/> rather than
    /// re-running the stock movement + cost emission.
    /// </summary>
    Task<InventoryTransferSummary> ShipAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        DateOnly postingDate,
        CancellationToken cancellationToken,
        string? idempotencyKey = null);

    /// <summary>Receive the transfer. Same idempotency semantics as ShipAsync.</summary>
    Task<InventoryTransferSummary> ReceiveAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        DateOnly postingDate,
        CancellationToken cancellationToken,
        string? idempotencyKey = null);
}
