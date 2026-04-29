namespace Citus.Business.Blazor.Components.Shared;

/// <summary>
/// Five-field postal address used by AddressEditor + AddressDisplay
/// across Quote / Sales Order / Invoice / Bill / PO / Expense pages.
/// All fields nullable so an unset address renders as "no address yet"
/// instead of forcing every page to manage empty-string defaults.
/// </summary>
public sealed record AddressDraft
{
    public string? AddressLine { get; init; }

    public string? City { get; init; }

    public string? ProvinceState { get; init; }

    public string? PostalCode { get; init; }

    public string? Country { get; init; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(AddressLine) &&
        string.IsNullOrWhiteSpace(City) &&
        string.IsNullOrWhiteSpace(ProvinceState) &&
        string.IsNullOrWhiteSpace(PostalCode) &&
        string.IsNullOrWhiteSpace(Country);

    public static AddressDraft Empty => new();
}

public enum ShippingMode
{
    SameAsBilling,
    Different,
}

/// <summary>
/// Composite returned by the AddressEditor drawer when the operator
/// hits Save. The page reads .Mode and decides whether to copy
/// Billing into Shipping or use Shipping as-is when persisting the
/// hosting document (quote / SO / invoice / etc).
/// </summary>
public sealed record AddressEditResult(
    AddressDraft Billing,
    ShippingMode Mode,
    AddressDraft Shipping);

public sealed record CustomerShippingAddressSuggestion(
    string? AddressLine,
    string? City,
    string? ProvinceState,
    string? PostalCode,
    string? Country,
    int UsageCount,
    DateOnly LastUsedOn);
