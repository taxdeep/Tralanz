namespace Citus.Business.Blazor.Components.Features.Invoices;

/// <summary>
/// UI-side per-line state for the invoice editor. Carries the wire-shape
/// fields plus the item-picker's <see cref="ItemId"/> / <see cref="ItemDisplay"/>
/// so the picker on each row can render a stable "selected" state across
/// re-renders. Promoted from a page-private record to a shared public record
/// (no logic change) so <c>InvoiceEditor</c> and <c>InvoiceLineGrid</c> can
/// both reference it. Submit projects this list back into the wire shape;
/// ItemId is informational for now.
/// </summary>
public sealed record InvoiceLineEditState(
    Guid ItemId = default,
    string ItemDisplay = "",
    string Description = "",
    decimal Quantity = 1m,
    decimal UnitPrice = 0m,
    Guid? AccountId = null,
    string AccountDisplay = "",
    string AccountCode = "",
    Guid? TaxCodeId = null,
    string TaxCode = "",
    Guid? TaxCodeSetId = null,
    // Back-link to the Task this line bills. Set on every line when
    // the page is opened via "Bill this task" (FromTaskId path). The
    // submit projection passes it to the wire shape so the post
    // handler can flip the source tasks Completed -> Billed.
    Guid? TaskId = null,
    // Q1: SO-derived lines lock their item / category / qty / unit price
    // (the SO defines the commitment). Set true for the lines carried
    // from a source Sales Order; operator-added lines stay false/editable.
    bool Locked = false)
{
    public bool HasContent =>
        ItemId != Guid.Empty
        || AccountId is not null
        || !string.IsNullOrWhiteSpace(Description)
        || !string.IsNullOrWhiteSpace(AccountCode)
        || !string.IsNullOrWhiteSpace(TaxCode)
        || UnitPrice > 0m;
}
