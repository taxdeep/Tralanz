using Citus.Accounting.Api;
using Citus.Accounting.Api.Endpoints;
using Citus.Accounting.Api.Startup;
using static Citus.Accounting.Api.AccountingEndpointHelpers;
using static Citus.Accounting.Api.CompanyCurrencyResponseMapper;
using static Citus.Accounting.Api.InventoryItemRequestMapper;
using static Citus.Accounting.Api.Authorization.EndpointApprovalAuthorityHelpers;
using static Citus.Accounting.Api.Endpoints.Support.ReviewMappers;
using static Citus.Accounting.Api.Endpoints.Support.BusinessSessionEndpointHelpers;
using Citus.Accounting.Api.Initialization;
using Citus.Accounting.Api.Tasks;
using Citus.Accounting.Application;
using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.CoaTemplates;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Companies;
using Citus.Accounting.Application.Invoices;
using Citus.Accounting.Application.Queries;
using Citus.Accounting.Application.Statements;
using Citus.Accounting.Application.Reconciliation;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Infrastructure.Persistence;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Journal;
using Citus.Ui.Shared.Reports;
using Citus.Ui.Shared.Shell;
using Citus.Accounting.Infrastructure.Companies;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Invoices;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Citus.Accounting.Infrastructure.Statements;
using Citus.Modules.UnitySearch.Application;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Application.Pricing;
using Citus.Modules.Inventory.Domain.Shared;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using Citus.Modules.Tasks.Application;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.Tasks.Domain.Shared.Reports;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Accounts;
using Infrastructure.PostgreSQL.Uom;
using Infrastructure.PostgreSQL.BusinessAuth;
using Infrastructure.PostgreSQL.Banking;
using Infrastructure.PostgreSQL.Company;
using Infrastructure.PostgreSQL.CompanyAccess;
using Infrastructure.PostgreSQL.AP.Bills;
using Infrastructure.PostgreSQL.AP.Expenses;
using Infrastructure.PostgreSQL.AP.PurchaseOrders;
using Infrastructure.PostgreSQL.Counterparties;
using Infrastructure.PostgreSQL.Sales;
using Modules.AP.Bills;
using Modules.AP.Expenses;
using Modules.AP.PurchaseOrders;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Inventory;
using Infrastructure.PostgreSQL.Inventory.Posting;
using Infrastructure.PostgreSQL.Numbering;
using Infrastructure.PostgreSQL.Tax;
using Infrastructure.PostgreSQL.UnitySearch;
using Infrastructure.PostgreSQL.UnityAi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Citus.Accounting.Api.Authorization;
using Modules.CompanyAccess.Memberships;
using Modules.CompanyAccess.SessionContext;
using Npgsql;
using Modules.Company.FeatureManagement;
using Modules.Company.MultiBook;
using Modules.Company.MultiCurrency;
using System.Text;
using System.Threading.RateLimiting;
using JournalEntryNumberLookup = Engines.Numbering.JournalEntry.IJournalEntryNumberLookup;
using GlIJournalEntryLifecycleStore = Modules.GL.JournalEntry.IJournalEntryLifecycleStore;
using GlIJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.IJournalEntryLifecycleWorkflow;
using GlJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.JournalEntryLifecycleWorkflow;

namespace Citus.Accounting.Api.Endpoints.Support;

/// <summary>
/// Endpoint request validators / input mappers / small projection helpers
/// extracted verbatim from Program.cs (P6a) so the per-domain endpoint modules
/// (P6) can share them. Behavior unchanged; only made public + namespaces
/// globalized.
/// </summary>
public static class EndpointRequestHelpers
{
    internal static TaxCodeSetUpsertInput MapTaxCodeSetInput(TaxCodeSetUpsertHttpRequest request)
        => new(
            Code: request.Code!.Trim(),
            Name: request.Name!.Trim(),
            AppliesTo: request.AppliesTo!.Trim().ToLowerInvariant(),
            IsActive: request.IsActive ?? true,
            Members: (request.Members ?? Array.Empty<TaxCodeSetMemberHttpRequest>())
                .Select((m, i) => new TaxCodeSetMemberInput(m.RuleId, m.Sequence > 0 ? m.Sequence : i + 1, m.IsCompound))
                .ToList());

    internal static string? ValidateTaxCodeSetInput(TaxCodeSetUpsertHttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) return "Code is required.";
        if (request.Code.Length > 32) return "Code must be 32 characters or fewer.";
        if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
        if (request.Name.Length > 120) return "Name must be 120 characters or fewer.";
        if (!TaxCodeAppliesTo.IsValid(request.AppliesTo?.Trim().ToLowerInvariant())) return "Applies-to must be sales, purchase, or both.";
        var members = request.Members ?? Array.Empty<TaxCodeSetMemberHttpRequest>();
        if (members.Count == 0) return "A Tax Code must contain at least one Tax Rule.";
        if (members.Select(m => m.RuleId).Distinct().Count() != members.Count) return "A Tax Rule can appear at most once in a Tax Code.";
        return null;
    }

    internal static string? ValidateTaxCodeInput(TaxCodeUpsertHttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) return "Code is required.";
        if (request.Code.Length > 32) return "Code must be 32 characters or fewer.";
        if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
        if (request.Name.Length > 120) return "Name must be 120 characters or fewer.";
        if (request.RatePercent is null || request.RatePercent < 0m) return "Rate must be 0 or greater.";
        if (request.RatePercent > 100m) return "Rate must be 100 or lower.";
        if (string.IsNullOrWhiteSpace(request.AppliesTo) || !TaxCodeAppliesTo.IsValid(request.AppliesTo.Trim().ToLowerInvariant()))
        {
            return "Applies to must be 'sales', 'purchase', or 'both'.";
        }
        if (string.IsNullOrWhiteSpace(request.RegistrationNumber))
        {
            return "Tax registration number is required.";
        }
        if (request.RegistrationNumber.Length > 64)
        {
            return "Registration number must be 64 characters or fewer.";
        }
        return null;
    }

    internal static string? ValidatePaymentTermInput(PaymentTermUpsertHttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) return "Code is required.";
        if (request.Code.Length > 32) return "Code must be 32 characters or fewer.";
        if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
        if (request.Name.Length > 120) return "Name must be 120 characters or fewer.";
        if (request.NetDays is null || request.NetDays < 0) return "Net days must be 0 or greater.";
        if (request.NetDays > 3650) return "Net days must be 3650 or fewer.";
        return null;
    }

    internal static string? ValidateQuoteInput(QuoteUpsertHttpRequest request)
    {
        if (request.CustomerId == Guid.Empty) return "Customer is required.";
        if (string.IsNullOrWhiteSpace(request.TransactionCurrencyCode)) return "Transaction currency is required.";
        if (request.TransactionCurrencyCode.Length != 3) return "Transaction currency must be a 3-letter code.";
        var taxMode = string.IsNullOrWhiteSpace(request.TaxMode) ? QuoteTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant();
        if (!QuoteTaxMode.IsValid(taxMode)) return "Tax mode must be 'exclusive' or 'inclusive'.";
        if (request.Lines is null || request.Lines.Count == 0) return "At least one line is required.";
        foreach (var line in request.Lines)
        {
            if (line.Quantity < 0) return "Line quantity must be 0 or greater.";
            if (line.UnitPrice < 0) return "Line unit price must be 0 or greater.";
        }
        return null;
    }

    internal static object MapInvoiceTemplate(InvoiceTemplate template) => new
    {
        template.Id,
        template.CompanyId,
        template.Name,
        template.IsDefault,
        template.Config.LogoUrl,
        template.Config.PrimaryColorHex,
        template.Config.AccentColorHex,
        template.Config.Tagline,
        template.Config.Greeting,
        template.Config.PaymentInstructions,
        template.Config.FooterNote,
        template.Config.ShowTaxColumn,
        template.Config.EmailSubjectTemplate,
        template.Config.EmailBodyTemplate,
        template.CreatedAt,
        template.UpdatedAt,
    };

    internal static (InvoiceTemplateConfig Config, string? Error) TryReadInvoiceTemplateConfig(
        InvoiceTemplateUpsertHttpRequest request)
    {
        var defaults = InvoiceTemplateConfig.Default;

        var primary = string.IsNullOrWhiteSpace(request.PrimaryColorHex)
            ? defaults.PrimaryColorHex
            : request.PrimaryColorHex!.Trim();
        if (!IsValidHexColor(primary))
        {
            return (defaults, $"Primary color '{primary}' is not a valid hex color (expected #RRGGBB).");
        }

        var accent = string.IsNullOrWhiteSpace(request.AccentColorHex)
            ? defaults.AccentColorHex
            : request.AccentColorHex!.Trim();
        if (!IsValidHexColor(accent))
        {
            return (defaults, $"Accent color '{accent}' is not a valid hex color (expected #RRGGBB).");
        }

        var config = new InvoiceTemplateConfig(
            LogoUrl: TrimToNull(request.LogoUrl),
            PrimaryColorHex: primary,
            AccentColorHex: accent,
            Tagline: TrimToNull(request.Tagline),
            Greeting: request.Greeting?.Trim() ?? defaults.Greeting,
            PaymentInstructions: request.PaymentInstructions?.Trim() ?? string.Empty,
            FooterNote: request.FooterNote?.Trim() ?? defaults.FooterNote,
            ShowTaxColumn: request.ShowTaxColumn ?? defaults.ShowTaxColumn,
            EmailSubjectTemplate: string.IsNullOrWhiteSpace(request.EmailSubjectTemplate)
                ? defaults.EmailSubjectTemplate
                : request.EmailSubjectTemplate!.Trim(),
            EmailBodyTemplate: request.EmailBodyTemplate?.Trim() ?? string.Empty);

        return (config, null);
    }

    internal static bool IsValidHexColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!value.StartsWith('#')) return false;
        if (value.Length is not (4 or 7 or 9)) return false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    internal static string? TrimToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    internal static InvoiceReviewProjection BuildSampleInvoiceProjection(string currencyCode)
    {
        var documentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        return new InvoiceReviewProjection(
            DisplayNumber: "INV-PREVIEW",
            EntityNumber: "EN0000PREVIEW",
            DocumentDate: documentDate,
            DueDate: documentDate.AddDays(30),
            Status: "preview",
            CounterpartyDisplayName: "Acme Co.",
            TransactionCurrencyCode: string.IsNullOrWhiteSpace(currencyCode) ? "USD" : currencyCode,
            SubtotalAmount: 175m,
            TaxAmount: 22.75m,
            TotalAmount: 197.75m,
            Memo: "Sample preview — replace with real invoice content when sending.",
            Lines:
            [
                new InvoiceReviewLineProjection(1, "Design retainer (sample)", 1m, 100m, 100m, 13m),
                new InvoiceReviewLineProjection(2, "Hosting (sample)", 3m, 25m, 75m, 9.75m),
            ]);
    }

    internal static QuoteUpsertInput MapQuoteInput(QuoteUpsertHttpRequest request) => new(
        CustomerId: request.CustomerId,
        DocumentDate: request.DocumentDate,
        ExpirationDate: request.ExpirationDate,
        TransactionCurrencyCode: request.TransactionCurrencyCode,
        FxRate: request.FxRate,
        BillingAddressLine: request.BillingAddressLine,
        BillingCity: request.BillingCity,
        BillingProvinceState: request.BillingProvinceState,
        BillingPostalCode: request.BillingPostalCode,
        BillingCountry: request.BillingCountry,
        ShippingAddressLine: request.ShippingAddressLine,
        ShippingCity: request.ShippingCity,
        ShippingProvinceState: request.ShippingProvinceState,
        ShippingPostalCode: request.ShippingPostalCode,
        ShippingCountry: request.ShippingCountry,
        ShipVia: request.ShipVia,
        ShippingDate: request.ShippingDate,
        TrackingNo: request.TrackingNo,
        TaxMode: string.IsNullOrWhiteSpace(request.TaxMode) ? QuoteTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant(),
        DiscountKind: request.DiscountKind,
        DiscountValue: request.DiscountValue,
        ShippingAmount: request.ShippingAmount,
        ShippingTaxCodeId: request.ShippingTaxCodeId,
        MemoToCustomer: request.MemoToCustomer,
        InternalNote: request.InternalNote,
        CustomerPoNumber: string.IsNullOrWhiteSpace(request.CustomerPoNumber) ? null : request.CustomerPoNumber.Trim(),
        Lines: (request.Lines ?? Array.Empty<QuoteLineHttpRequest>())
            .Select(l => new QuoteLineInput(
                Sequence: l.Sequence,
                ServiceDate: l.ServiceDate,
                ItemId: l.ItemId,
                Description: l.Description ?? string.Empty,
                Quantity: l.Quantity,
                UnitPrice: l.UnitPrice,
                TaxCodeId: l.TaxCodeId,
                TaxCodeSetId: l.TaxCodeSetId,
                AccountCode: l.AccountCode))
            .ToArray(),
        ExpectedUpdatedAt: request.ExpectedUpdatedAt);

    internal static string? ValidateSalesOrderInput(SalesOrderUpsertHttpRequest request)
    {
        if (request.CustomerId == Guid.Empty) return "Customer is required.";
        if (string.IsNullOrWhiteSpace(request.TransactionCurrencyCode)) return "Transaction currency is required.";
        if (request.TransactionCurrencyCode.Length != 3) return "Transaction currency must be a 3-letter code.";
        var taxMode = string.IsNullOrWhiteSpace(request.TaxMode) ? QuoteTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant();
        if (!QuoteTaxMode.IsValid(taxMode)) return "Tax mode must be 'exclusive' or 'inclusive'.";
        if (request.Lines is null || request.Lines.Count == 0) return "At least one line is required.";
        foreach (var line in request.Lines)
        {
            if (line.Quantity < 0) return "Line quantity must be 0 or greater.";
            if (line.UnitPrice < 0) return "Line unit price must be 0 or greater.";
        }
        return null;
    }

    internal static SalesOrderUpsertInput MapSalesOrderInput(SalesOrderUpsertHttpRequest request) => new(
        CustomerId: request.CustomerId,
        DocumentDate: request.DocumentDate,
        TransactionCurrencyCode: request.TransactionCurrencyCode,
        FxRate: request.FxRate,
        BillingAddressLine: request.BillingAddressLine,
        BillingCity: request.BillingCity,
        BillingProvinceState: request.BillingProvinceState,
        BillingPostalCode: request.BillingPostalCode,
        BillingCountry: request.BillingCountry,
        ShippingAddressLine: request.ShippingAddressLine,
        ShippingCity: request.ShippingCity,
        ShippingProvinceState: request.ShippingProvinceState,
        ShippingPostalCode: request.ShippingPostalCode,
        ShippingCountry: request.ShippingCountry,
        ShipVia: request.ShipVia,
        ShippingDate: request.ShippingDate,
        TrackingNo: request.TrackingNo,
        TaxMode: string.IsNullOrWhiteSpace(request.TaxMode) ? QuoteTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant(),
        DiscountKind: request.DiscountKind,
        DiscountValue: request.DiscountValue,
        ShippingAmount: request.ShippingAmount,
        ShippingTaxCodeId: request.ShippingTaxCodeId,
        MemoToCustomer: request.MemoToCustomer,
        InternalNote: request.InternalNote,
        SourceQuoteId: request.SourceQuoteId,
        CustomerPoNumber: string.IsNullOrWhiteSpace(request.CustomerPoNumber) ? null : request.CustomerPoNumber.Trim(),
        Lines: (request.Lines ?? Array.Empty<SalesOrderLineHttpRequest>())
            .Select(l => new SalesOrderLineInput(
                Sequence: l.Sequence,
                ServiceDate: l.ServiceDate,
                ItemId: l.ItemId,
                Description: l.Description ?? string.Empty,
                Quantity: l.Quantity,
                UnitPrice: l.UnitPrice,
                TaxCodeId: l.TaxCodeId,
                TaxCodeSetId: l.TaxCodeSetId,
                AccountCode: l.AccountCode))
            .ToArray(),
        ExpectedUpdatedAt: request.ExpectedUpdatedAt);

    internal static string? ValidateBillInput(BillUpsertHttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BillNumber)) return "Bill number is required (use the supplier's invoice number).";
        if (request.BillNumber.Length > 64) return "Bill number must be 64 characters or fewer.";
        if (request.VendorId == Guid.Empty) return "Vendor is required.";
        if (string.IsNullOrWhiteSpace(request.DocumentCurrencyCode)) return "Document currency is required.";
        if (request.DocumentCurrencyCode.Length != 3) return "Document currency must be a 3-letter code.";
        if (request.DueDate < request.BillDate) return "Due date cannot be before bill date.";
        if (request.FxRate is { } rate && rate <= 0m) return "Exchange rate must be greater than zero.";
        if (request.Lines is null || request.Lines.Count == 0) return "At least one line is required.";
        foreach (var line in request.Lines)
        {
            if (line.ExpenseAccountId == Guid.Empty) return "Each line must point to a category account.";
            if (line.LineAmount < 0m) return "Line amount must be 0 or greater.";
            if (line.TaxAmount is { } tax && tax < 0m) return "Tax amount must be 0 or greater.";
        }
        return null;
    }

    internal static async Task ValidateBillExpenseTaskLinksAsync(
        ITaskLineLinkValidator validator,
        CompanyId companyId,
        IEnumerable<Guid?> lineTaskIds,
        CancellationToken cancellationToken)
    {
        var distinct = lineTaskIds
            .Where(static id => id.HasValue && id.Value != Guid.Empty)
            .Select(static id => id!.Value)
            .Distinct()
            .ToArray();
        foreach (var taskId in distinct)
        {
            await validator.ValidateAsync(companyId, taskId, cancellationToken);
        }
    }

    internal static BillUpsertInput MapBillInput(BillUpsertHttpRequest request) =>
        new(
            BillNumber: request.BillNumber,
            VendorId: request.VendorId,
            BillDate: request.BillDate,
            DueDate: request.DueDate,
            DocumentCurrencyCode: request.DocumentCurrencyCode,
            FxRate: request.FxRate,
            Memo: request.Memo,
            PaymentTermId: request.PaymentTermId,
            SourcePurchaseOrderId: request.SourcePurchaseOrderId,
            SourcePurchaseOrderNumber: request.SourcePurchaseOrderNumber,
            Lines: (request.Lines ?? Array.Empty<BillLineHttpRequest>())
                .Select(l => new BillLineInput(
                    LineNumber: l.LineNumber,
                    ExpenseAccountId: l.ExpenseAccountId,
                    Description: l.Description ?? string.Empty,
                    LineAmount: l.LineAmount,
                    TaxCodeId: l.TaxCodeId,
                    TaxAmount: l.TaxAmount ?? 0m,
                    TaskId: l.TaskId))
                .ToArray(),
            ExpectedUpdatedAt: request.ExpectedUpdatedAt,
            // Copy A3 Phase 2: thread the source bill id through so the
            // store writes the bill_copied audit_log row.
            CopiedFromBillId: request.CopiedFromBillId);

    internal static string? ValidatePurchaseOrderInput(PurchaseOrderUpsertHttpRequest request)
    {
        if (request.VendorId == Guid.Empty) return "Vendor is required.";
        if (string.IsNullOrWhiteSpace(request.TransactionCurrencyCode)) return "Transaction currency is required.";
        if (request.TransactionCurrencyCode.Length != 3) return "Transaction currency must be a 3-letter code.";
        var taxMode = string.IsNullOrWhiteSpace(request.TaxMode) ? PurchaseOrderTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant();
        if (!PurchaseOrderTaxMode.IsValid(taxMode)) return "Tax mode must be 'exclusive' or 'inclusive'.";
        if (request.Lines is null || request.Lines.Count == 0) return "At least one line is required.";
        foreach (var line in request.Lines)
        {
            if (line.ExpenseAccountId is null && line.ItemId is null)
                return "Each line must have a category (Item-mode lines land with the Inventory batch).";
            if (line.Quantity < 0) return "Line quantity must be 0 or greater.";
            if (line.UnitPrice < 0) return "Line unit price must be 0 or greater.";
        }
        return null;
    }

    internal static PurchaseOrderUpsertInput MapPurchaseOrderInput(PurchaseOrderUpsertHttpRequest request) => new(
        VendorId: request.VendorId,
        OrderDate: request.OrderDate,
        ExpectedDeliveryDate: request.ExpectedDeliveryDate,
        TransactionCurrencyCode: request.TransactionCurrencyCode,
        FxRate: request.FxRate,
        BillingAddressLine: request.BillingAddressLine,
        BillingCity: request.BillingCity,
        BillingProvinceState: request.BillingProvinceState,
        BillingPostalCode: request.BillingPostalCode,
        BillingCountry: request.BillingCountry,
        ShippingAddressLine: request.ShippingAddressLine,
        ShippingCity: request.ShippingCity,
        ShippingProvinceState: request.ShippingProvinceState,
        ShippingPostalCode: request.ShippingPostalCode,
        ShippingCountry: request.ShippingCountry,
        ShipVia: request.ShipVia,
        ShippingDate: request.ShippingDate,
        TrackingNo: request.TrackingNo,
        TaxMode: string.IsNullOrWhiteSpace(request.TaxMode) ? PurchaseOrderTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant(),
        DiscountKind: request.DiscountKind,
        DiscountValue: request.DiscountValue,
        ShippingAmount: request.ShippingAmount,
        ShippingTaxCodeId: request.ShippingTaxCodeId,
        MemoToSupplier: request.MemoToSupplier,
        InternalNote: request.InternalNote,
        PaymentTermId: request.PaymentTermId,
        Lines: (request.Lines ?? Array.Empty<PurchaseOrderLineHttpRequest>())
            .Select(l => new PurchaseOrderLineInput(
                Sequence: l.Sequence,
                ServiceDate: l.ServiceDate,
                ItemId: l.ItemId,
                ExpenseAccountId: l.ExpenseAccountId,
                Description: l.Description ?? string.Empty,
                Quantity: l.Quantity,
                UnitPrice: l.UnitPrice,
                TaxCodeId: l.TaxCodeId))
            .ToArray(),
        ExpectedUpdatedAt: request.ExpectedUpdatedAt);

    internal static string? ValidateExpenseInput(ExpenseUpsertHttpRequest request)
    {
        if (!ExpensePayeeKind.IsValid(request.PayeeKind))
            return "Payee kind must be 'vendor', 'employee', or 'other'.";
        if (request.PayeeId is null && string.IsNullOrWhiteSpace(request.PayeeNameFreeform))
            return "Payee is required (pick a vendor / employee or enter a free-form name).";
        if (request.PaymentAccountId == Guid.Empty)
            return "Payment account is required.";
        if (!ExpensePaymentMethod.IsValid(request.PaymentMethod))
            return "Invalid payment method.";

        var refValidation = ExpensePaymentMethod.ValidateReferenceFields(request.PaymentMethod, request.ChequeNumber, request.RefNo);
        if (refValidation is not null) return refValidation;

        if (string.IsNullOrWhiteSpace(request.TransactionCurrencyCode)) return "Transaction currency is required.";
        if (request.TransactionCurrencyCode.Length != 3) return "Transaction currency must be a 3-letter code.";
        if (request.FxRate is { } rate && rate <= 0m) return "Exchange rate must be greater than zero.";

        var taxMode = string.IsNullOrWhiteSpace(request.TaxMode) ? ExpenseTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant();
        if (!ExpenseTaxMode.IsValid(taxMode)) return "Tax mode must be 'exclusive' or 'inclusive'.";

        if (request.Lines is null || request.Lines.Count == 0) return "At least one line is required.";
        foreach (var line in request.Lines)
        {
            if (line.ExpenseAccountId == Guid.Empty) return "Each line must point to a category account.";
            if (line.Quantity < 0) return "Line quantity must be 0 or greater.";
            if (line.UnitPrice < 0) return "Line unit price must be 0 or greater.";
        }
        return null;
    }

    internal static ExpenseUpsertInput MapExpenseInput(ExpenseUpsertHttpRequest request) => new(
        PayeeKind: request.PayeeKind,
        PayeeId: request.PayeeId,
        PayeeNameFreeform: request.PayeeNameFreeform ?? string.Empty,
        PaymentAccountId: request.PaymentAccountId,
        PaymentMethod: request.PaymentMethod,
        ChequeNumber: string.IsNullOrWhiteSpace(request.ChequeNumber) ? null : request.ChequeNumber.Trim(),
        RefNo: string.IsNullOrWhiteSpace(request.RefNo) ? null : request.RefNo.Trim(),
        TransactionCurrencyCode: request.TransactionCurrencyCode,
        FxRate: request.FxRate,
        PaymentDate: request.PaymentDate,
        SourcePurchaseOrderId: request.SourcePurchaseOrderId,
        SourcePurchaseOrderNumber: request.SourcePurchaseOrderNumber,
        TaxMode: string.IsNullOrWhiteSpace(request.TaxMode) ? ExpenseTaxMode.Exclusive : request.TaxMode.Trim().ToLowerInvariant(),
        DiscountKind: request.DiscountKind,
        DiscountValue: request.DiscountValue,
        Memo: request.Memo,
        InternalNote: request.InternalNote,
        Lines: (request.Lines ?? Array.Empty<ExpenseLineHttpRequest>())
            .Select(l => new ExpenseLineInput(
                Sequence: l.Sequence,
                ServiceDate: l.ServiceDate,
                ItemId: l.ItemId,
                ExpenseAccountId: l.ExpenseAccountId,
                Description: l.Description ?? string.Empty,
                Quantity: l.Quantity,
                UnitPrice: l.UnitPrice,
                TaxCodeId: l.TaxCodeId,
                TaskId: l.TaskId,
                TaxCodeSetId: l.TaxCodeSetId))
            .ToArray(),
        // Copy A3 Phase 1: thread through to the store so it writes the
        // expense_copied audit_log row alongside the regular CREATE.
        CopiedFromExpenseId: request.CopiedFromExpenseId);

    internal static string? ValidateBankReconciliationRequest(BankReconciliationCompleteHttpRequest request)
    {
        if (request.BankAccountId == Guid.Empty) return "Statement account is required.";
        if (request.StatementDate == default) return "Statement date is required.";
        if (request.LedgerEntryIds is null || request.LedgerEntryIds.Count == 0) return "Select at least one ledger entry.";
        if (request.LedgerEntryIds.Any(id => id == Guid.Empty)) return "Selected ledger entries must be valid ids.";
        return null;
    }

    internal static string? ValidateBankReconciliationDraftOpen(BankReconciliationDraftOpenHttpRequest request)
    {
        if (request.BankAccountId == Guid.Empty) return "Statement account is required.";
        if (request.StatementDate == default) return "Statement date is required.";
        return null;
    }

    internal static IResult? RequireBankReconciliationAuthority(
        BusinessSessionContext? session,
        string transitionCode)
    {
        var decision = BusinessApprovalAuthority.EvaluateBankReconciliationAccess(session, transitionCode);
        if (decision.Allowed)
        {
            return null;
        }

        return Results.Json(
            new
            {
                message = decision.Message,
                outcomeCode = decision.OutcomeCode
            },
            statusCode: decision.OutcomeCode == "blocked_session_required"
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden);
    }

    internal static string? ValidateAccountInput(AccountUpsertHttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) return "Code is required.";
        if (request.Code.Length > 32) return "Code must be 32 characters or fewer.";
        if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
        if (request.Name.Length > 200) return "Name must be 200 characters or fewer.";
        if (string.IsNullOrWhiteSpace(request.RootType) || !AccountRootType.IsValid(request.RootType.Trim().ToLowerInvariant()))
        {
            return "Root type must be one of: asset, liability, equity, revenue, cost_of_sales, expense.";
        }
        if (request.DetailType is { Length: > 80 }) return "Detail type must be 80 characters or fewer.";
        if (!string.IsNullOrWhiteSpace(request.CurrencyCode))
        {
            var c = request.CurrencyCode.Trim();
            if (c.Length != 3) return "Currency code must be exactly 3 letters (ISO 4217).";
        }
        return null;
    }

    internal static string? BuildCreditMemoMemo(CreditMemoSaveAndPostHttpRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Reason)) parts.Add($"Reason: {request.Reason.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.AppliedToInvoiceNumber)) parts.Add($"Applied to {request.AppliedToInvoiceNumber.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.Memo)) parts.Add(request.Memo.Trim());
        return parts.Count == 0 ? null : string.Join(" — ", parts);
    }

    internal static string? BuildVendorCreditMemo(VendorCreditSaveAndPostHttpRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Reason)) parts.Add($"Reason: {request.Reason.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.AppliedToBillNumber)) parts.Add($"Applied to {request.AppliedToBillNumber.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.Memo)) parts.Add(request.Memo.Trim());
        return parts.Count == 0 ? null : string.Join(" — ", parts);
    }
}
