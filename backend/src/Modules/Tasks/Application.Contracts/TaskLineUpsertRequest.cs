namespace Citus.Modules.Tasks.Application.Contracts;

/// <summary>
/// Add / update one line.
/// <see cref="UnitPrice"/> may be null on add — the workflow then
/// consults <c>IItemPriceResolver</c> with the task's customer /
/// currency / service date. If the resolver returns nothing, the
/// workflow throws and the caller must supply a price explicitly.
/// </summary>
public sealed record class TaskLineUpsertRequest
{
    public required Guid ItemId { get; init; }

    public string? Description { get; init; }

    public required decimal Quantity { get; init; }

    public decimal? UnitPrice { get; init; }

    public Guid? TaxCodeId { get; init; }
}
