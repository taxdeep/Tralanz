using Citus.Accounting.Application.Repositories;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Ui.Shared.Journal;

namespace Citus.Accounting.Api.Endpoints.Support;

/// <summary>
/// Pure response/label mappers extracted verbatim from Program.cs (P1,
/// behavior-preserving): document-review labels, the invoice-coverage summary
/// string, and the journal-entry review read-model mappers.
/// </summary>
public static class ReviewMappers
{
    public static string MapDocumentReviewSourceLabel(string sourceType) =>
        sourceType switch
        {
            "manual_journal" => "Manual Journal",
            "invoice" => "Invoice",
            "credit_note" => "Credit Note",
            "bill" => "Bill",
            "vendor_credit" => "Vendor Credit",
            "receive_payment" => "Receive Payment",
            "credit_application" => "Credit Application",
            "pay_bill" => "Pay Bill",
            "vendor_credit_application" => "Vendor Credit Application",
            "invoice_reversal" => "Invoice Reversal",
            "credit_note_reversal" => "Credit Note Reversal",
            "bill_reversal" => "Bill Reversal",
            "vendor_credit_reversal" => "Vendor Credit Reversal",
            "receive_payment_reversal" => "Receive Payment Reversal",
            "credit_application_reversal" => "Credit Application Reversal",
            "pay_bill_reversal" => "Pay Bill Reversal",
            "vendor_credit_application_reversal" => "Vendor Credit Application Reversal",
            _ => "Document"
        };

    public static string MapDocumentReviewCounterpartyLabel(string counterpartyRole) =>
        counterpartyRole switch
        {
            "journal" => "Journal context",
            "customer" => "Customer",
            "vendor" => "Vendor",
            _ => "Counterparty"
        };

    public static string MapDocumentReviewControlAccountLabel(string counterpartyRole) =>
        counterpartyRole switch
        {
            "journal" => "Balancing logic",
            "customer" => "Receivable account",
            "vendor" => "Payable account",
            _ => "Control account"
        };

    public static string MapDocumentReviewLineAccountLabel(string counterpartyRole) =>
        counterpartyRole switch
        {
            "journal" => "Journal account",
            "customer" => "Revenue account",
            "vendor" => "Expense account",
            _ => "Account"
        };

    public static string BuildInvoiceCoverageSummary(InventoryInvoiceShipmentPostingGateSnapshot snapshot) =>
        snapshot.InvoiceCoverageStatus switch
        {
            "no_inventory_handoff" => "No shipped/invoiced coverage lane is active for this invoice.",
            "no_shipment" => "Shipment truth has not started yet, so nothing is formally invoiced against shipped quantity.",
            "not_invoiced" => $"Shipment truth exists, but {snapshot.RemainingToInvoiceQuantity:N2} shipped quantity still has no formal AR coverage.",
            "partially_invoiced" => $"{snapshot.RemainingToInvoiceQuantity:N2} shipped quantity is still waiting for formal AR coverage.",
            "fully_invoiced" => "Current shipped quantity is fully covered by posted AR truth.",
            "over_invoiced" => "Posted invoice truth currently exceeds shipped quantity and should move into discrepancy review.",
            _ => "Invoice coverage truth has not been evaluated yet."
        };

    public static JournalEntryReviewListItemSummary MapJournalEntryReviewListItem(JournalEntryReviewListItem item) =>
        new()
        {
            Id = item.Id,
            CompanyId = item.CompanyId,
            EntityNumber = item.EntityNumber,
            DisplayNumber = item.DisplayNumber,
            Status = item.Status,
            SourceType = item.SourceType,
            SourceTypeLabel = MapJournalEntrySourceTypeLabel(item.SourceType),
            SourceId = item.SourceId,
            TransactionCurrencyCode = item.TransactionCurrencyCode,
            BaseCurrencyCode = item.BaseCurrencyCode,
            TotalTxDebit = item.TotalTxDebit,
            TotalTxCredit = item.TotalTxCredit,
            TotalDebit = item.TotalDebit,
            TotalCredit = item.TotalCredit,
            LineCount = item.LineCount,
            PostedAt = item.PostedAt,
            VoidedAt = item.VoidedAt,
            ReversedAt = item.ReversedAt
        };

    public static JournalEntryReviewSummary MapJournalEntryReview(JournalEntryReview review) =>
        new()
        {
            Id = review.Id,
            CompanyId = review.CompanyId,
            EntityNumber = review.EntityNumber,
            DisplayNumber = review.DisplayNumber,
            Status = review.Status,
            SourceType = review.SourceType,
            SourceTypeLabel = MapJournalEntrySourceTypeLabel(review.SourceType),
            SourceId = review.SourceId,
            TransactionCurrencyCode = review.TransactionCurrencyCode,
            BaseCurrencyCode = review.BaseCurrencyCode,
            ExchangeRate = review.ExchangeRate,
            ExchangeRateDate = review.ExchangeRateDate,
            ExchangeRateSource = review.ExchangeRateSource,
            FxRateSnapshotId = review.FxRateSnapshotId,
            TotalTxDebit = review.TotalTxDebit,
            TotalTxCredit = review.TotalTxCredit,
            TotalDebit = review.TotalDebit,
            TotalCredit = review.TotalCredit,
            LineCount = review.LineCount,
            PostedAt = review.PostedAt,
            VoidedAt = review.VoidedAt,
            ReversedAt = review.ReversedAt,
            CreatedByUserId = review.CreatedByUserId,
            Lines = review.Lines.Select(MapJournalEntryReviewLine).ToArray()
        };

    public static JournalEntryReviewLineSummary MapJournalEntryReviewLine(JournalEntryReviewLine line) =>
        new()
        {
            LineId = line.LineId,
            LineNumber = line.LineNumber,
            AccountId = line.AccountId,
            AccountCode = line.AccountCode,
            AccountName = line.AccountName,
            RootType = line.RootType,
            DetailType = line.DetailType,
            Description = line.Description,
            TxDebit = line.TxDebit,
            TxCredit = line.TxCredit,
            Debit = line.Debit,
            Credit = line.Credit,
            TaxComponentType = line.TaxComponentType,
            ControlRole = line.ControlRole,
            PartyId = line.PartyId,
            PostingRole = line.PostingRole,
            SourceLineNumber = line.SourceLineNumber
        };

    public static string MapJournalEntrySourceTypeLabel(string sourceType) =>
        sourceType switch
        {
            "manual_journal" => "Manual Journal",
            "invoice" => "Invoice",
            "credit_note" => "Credit Note",
            "bill" => "Bill",
            "vendor_credit" => "Vendor Credit",
            "receive_payment" => "Receive Payment",
            "credit_application" => "Credit Application",
            "pay_bill" => "Pay Bill",
            "vendor_credit_application" => "Vendor Credit Application",
            "invoice_reversal" => "Invoice Reversal",
            "credit_note_reversal" => "Credit Note Reversal",
            "bill_reversal" => "Bill Reversal",
            "vendor_credit_reversal" => "Vendor Credit Reversal",
            "receive_payment_reversal" => "Receive Payment Reversal",
            "credit_application_reversal" => "Credit Application Reversal",
            "pay_bill_reversal" => "Pay Bill Reversal",
            "vendor_credit_application_reversal" => "Vendor Credit Application Reversal",
            "fx_revaluation" => "FX Revaluation",
            _ => "Source Document"
        };
}
