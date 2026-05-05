using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Modules.Inventory.Application;

public sealed class InventoryReceiptWorkflow
{
    private readonly IInventoryReceiptStore _store;

    public InventoryReceiptWorkflow(IInventoryReceiptStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<InventoryPurchaseReceiptDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        return _store.GetDashboardAsync(companyId, cancellationToken);
    }

    public Task<InventoryBillReceiptHandoffSummary> GetBillHandoffSummaryAsync(
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(companyId));
        }

        if (billDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Bill document id is required.", nameof(billDocumentId));
        }

        return _store.GetBillHandoffSummaryAsync(companyId, billDocumentId, cancellationToken);
    }

    public async Task<InventoryPurchaseReceiptSummary> PostAsync(
        InventoryPurchaseReceiptPostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CompanyId.Value is null)
        {
            throw new ArgumentException("Company id is required.", nameof(request));
        }

        if (request.UserId.Value is null)
        {
            throw new ArgumentException("User id is required.", nameof(request));
        }

        await GuardLegacyReceiptPathAsync(request, cancellationToken);

        if (request.VendorId == Guid.Empty)
        {
            throw new ArgumentException("Vendor id is required.", nameof(request));
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new InvalidOperationException("At least one purchase receipt line is required.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in request.Lines)
        {
            if (line.LineNo <= 0)
            {
                throw new InvalidOperationException("Purchase receipt line numbers must be positive.");
            }

            if (!seenLineNumbers.Add(line.LineNo))
            {
                throw new InvalidOperationException("Purchase receipt line numbers must be unique.");
            }

            if (line.ItemId == Guid.Empty)
            {
                throw new InvalidOperationException("Each purchase receipt line must select an inventory item.");
            }

            if (line.WarehouseId == Guid.Empty)
            {
                throw new InvalidOperationException("Each purchase receipt line must select a warehouse.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                throw new InvalidOperationException("Each purchase receipt line must include a UOM code.");
            }

            if (line.Quantity <= 0)
            {
                throw new InvalidOperationException("Purchase receipt quantities must be positive.");
            }

            if (line.UnitCostTx < 0)
            {
                throw new InvalidOperationException("Purchase receipt unit cost cannot be negative.");
            }
        }

        if (string.IsNullOrWhiteSpace(request.TransactionCurrencyCode))
        {
            throw new InvalidOperationException("A transaction currency code is required.");
        }

        if (request.FxRateToBase <= 0)
        {
            throw new InvalidOperationException("FX rate to base must be greater than zero.");
        }

        return await _store.PostAsync(request, cancellationToken);
    }

    private async Task GuardLegacyReceiptPathAsync(
        InventoryPurchaseReceiptPostRequest request,
        CancellationToken cancellationToken)
    {
        LegacyInboundReceiptPathSnapshot? snapshot = null;
        if (LegacyInboundReceiptPathPolicy.RequiresBillSnapshot(request))
        {
            snapshot = await _store.GetLegacyInboundReceiptPathSnapshotAsync(
                request.CompanyId,
                request.SourceDocumentId!.Value,
                cancellationToken);
        }

        var decision = LegacyInboundReceiptPathPolicy.Evaluate(request, snapshot);
        if (!decision.IsAllowed)
        {
            throw new InvalidOperationException(decision.Message);
        }
    }
}
